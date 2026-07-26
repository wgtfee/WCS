namespace Wcs.Core.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// PLC 机器学习异常引擎：窗口特征、训练数据采集、Isolation Forest 训练、版本治理与在线生命周期。
/// </summary>
public sealed class PlcMlAnomalyEngine : IPlcMlAnomalyEngine
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly PlcFeatureWindowEngine _windowEngine;
    private readonly IPlcMlModelStore _modelStore;
    private readonly IPlcMlTrainingStore _trainingStore;
    private readonly IPlcMlGovernanceStore _governanceStore;
    private readonly IEventBus _eventBus;
    private readonly IReadOnlyDictionary<string, PlcMlProfile> _profiles;
    private readonly ConcurrentDictionary<string, PlcIsolationForestModel> _models = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InferenceState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProfileRuntimeMetrics> _metrics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private int _initialized;

    public PlcMlAnomalyEngine(
        PlcMlAnomalyOptions options,
        PlcFeatureWindowEngine windowEngine,
        IPlcMlModelStore modelStore,
        IPlcMlTrainingStore trainingStore,
        IPlcMlGovernanceStore governanceStore,
        IEventBus eventBus)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windowEngine = windowEngine ?? throw new ArgumentNullException(nameof(windowEngine));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _trainingStore = trainingStore ?? throw new ArgumentNullException(nameof(trainingStore));
        _governanceStore = governanceStore ?? throw new ArgumentNullException(nameof(governanceStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _profiles = options.Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);

        foreach (var profile in options.Profiles)
        {
            _metrics.TryAdd(profile.ProfileId, new ProfileRuntimeMetrics());
            _profileLocks.TryAdd(profile.ProfileId, new SemaphoreSlim(1, 1));
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _initialized) == 1) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized == 1) return;
            foreach (var profile in _options.Profiles.Where(static profile => profile.Enabled))
            {
                try
                {
                    var governanceModels = await _governanceStore.ListModelsAsync(profile.ProfileId, cancellationToken);
                    var pending = governanceModels.FirstOrDefault(model => model.ApprovalStatus == PlcMlApprovalStatus.Pending);
                    _metrics[profile.ProfileId].SetPendingModel(pending?.ModelVersion);

                    var model = await _modelStore.LoadActiveAsync(profile.ProfileId, cancellationToken);
                    if (model is not null)
                    {
                        ValidateModel(profile, model);
                        if (!profile.RequireModelApproval ||
                            await _governanceStore.IsModelApprovedAsync(profile.ProfileId, model.Version, cancellationToken))
                        {
                            _models[profile.ProfileId] = model;
                        }
                        else
                        {
                            _metrics[profile.ProfileId].RecordFailure(new InvalidOperationException(
                                $"活动模型 {model.Version} 尚未审批，未加载到 Profile {profile.ProfileId}。"));
                        }
                    }

                    var count = await _trainingStore.CountAsync(profile.ProfileId, cancellationToken);
                    _metrics[profile.ProfileId].SetTrainingCount(count);
                }
                catch (Exception ex)
                {
                    _metrics[profile.ProfileId].RecordFailure(ex);
                }
            }
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async ValueTask ProcessAsync(
        PlcAnomalySample sample,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken);
        foreach (var vector in _windowEngine.Process(sample))
            await ProcessVectorAsync(vector, cancellationToken);
    }

    public async Task MaintenanceAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken);

        foreach (var vector in _windowEngine.FlushExpired(utcNow))
            await ProcessVectorAsync(vector, cancellationToken);

        var cutoff = utcNow.AddSeconds(-_options.InactiveInferenceStateRetentionSeconds);
        foreach (var pair in _states)
        {
            var state = pair.Value;
            var removable = false;
            lock (state.Gate)
                removable = state.Active is null && state.LastUpdatedUtc < cutoff;
            if (removable)
                ((ICollection<KeyValuePair<string, InferenceState>>)_states).Remove(pair);
        }

        foreach (var profile in _options.Profiles)
        {
            if (!profile.Enabled) continue;
            var runtime = _metrics[profile.ProfileId];
            var drift = runtime.TakePendingDriftSnapshot();
            if (drift is not null)
            {
                try
                {
                    await _governanceStore.SaveDriftSnapshotAsync(drift, cancellationToken);
                }
                catch (Exception ex)
                {
                    runtime.RecordFailure(ex);
                }
            }

            if (!profile.AutoTrain || _models.ContainsKey(profile.ProfileId)) continue;
            if (runtime.PendingModelVersion is not null) continue;
            if (runtime.TrainingCount < profile.MinimumTrainingWindows) continue;
            try
            {
                await TrainAsync(profile.ProfileId, cancellationToken);
            }
            catch (Exception ex)
            {
                runtime.RecordFailure(ex);
            }
        }
    }

    public Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        TrainAsync(profileId, datasetVersion: null, requestedBy: null, cancellationToken);

    public async Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        string? datasetVersion,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        var profile = GetProfile(profileId);
        var profileLock = _profileLocks[profileId];
        await profileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureNoActiveAnomalies(profileId, "训练新模型");
            var vectors = string.IsNullOrWhiteSpace(datasetVersion)
                ? await _trainingStore.ReadAsync(profileId, profile.MaximumTrainingWindows, cancellationToken)
                : await _trainingStore.ReadDatasetAsync(
                    profileId,
                    datasetVersion,
                    profile.MaximumTrainingWindows,
                    cancellationToken);
            var model = IsolationForest.Train(profile, vectors, DateTime.UtcNow);
            ValidateModel(profile, model);
            await _modelStore.SaveVersionAsync(model, cancellationToken);

            var governance = new PlcMlModelGovernanceInfo
            {
                GovernanceId = $"{profileId}|{model.Version}",
                ProfileId = profileId,
                ModelVersion = model.Version,
                DatasetVersion = string.IsNullOrWhiteSpace(datasetVersion) ? null : datasetVersion,
                ApprovalStatus = PlcMlApprovalStatus.Pending,
                RequestedUtc = model.CreatedUtc,
                RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "system" : requestedBy.Trim(),
                TrainingSampleCount = model.TrainingSampleCount,
                CalibrationSampleCount = model.CalibrationSampleCount,
                DecisionThreshold = model.DecisionThreshold
            };
            await _governanceStore.RegisterModelAsync(governance, cancellationToken);

            var activated = false;
            var approval = PlcMlApprovalStatus.Pending;
            if (!profile.RequireModelApproval)
            {
                await _governanceStore.DecideModelAsync(
                    profileId,
                    model.Version,
                    PlcMlApprovalStatus.Approved,
                    "system-auto-approval",
                    "Profile 未要求人工审批。",
                    DateTime.UtcNow,
                    cancellationToken);
                await _modelStore.ActivateAsync(model, cancellationToken);
                _models[profileId] = model;
                activated = true;
                approval = PlcMlApprovalStatus.Approved;
                _metrics[profileId].SetPendingModel(null);
            }
            else
            {
                _metrics[profileId].SetPendingModel(model.Version);
            }

            _metrics[profileId].SetTrainingCount(vectors.Count);
            return new PlcMlTrainingResult
            {
                ProfileId = profileId,
                ModelVersion = model.Version,
                DatasetVersion = string.IsNullOrWhiteSpace(datasetVersion) ? null : datasetVersion,
                TrainingSampleCount = model.TrainingSampleCount,
                CalibrationSampleCount = model.CalibrationSampleCount,
                TreeCount = model.Trees.Length,
                DecisionThreshold = model.DecisionThreshold,
                CreatedUtc = model.CreatedUtc,
                ApprovalStatus = approval,
                Activated = activated
            };
        }
        finally
        {
            profileLock.Release();
        }
    }

    public async Task<IReadOnlyList<PlcMlModelVersionInfo>> ListModelsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        GetProfile(profileId);
        return await _modelStore.ListAsync(profileId, cancellationToken);
    }

    public async Task<PlcMlModelVersionInfo> ActivateModelAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var profile = GetProfile(profileId);
        var profileLock = _profileLocks[profileId];
        await profileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureNoActiveAnomalies(profileId, "切换模型版本");
            if (profile.RequireModelApproval &&
                !await _governanceStore.IsModelApprovedAsync(profileId, version, cancellationToken))
                throw new InvalidOperationException($"模型 {profileId}/{version} 尚未审批，不能激活。");

            var model = await _modelStore.LoadVersionAsync(profileId, version, cancellationToken)
                ?? throw new KeyNotFoundException($"未找到模型：Profile={profileId}, Version={version}。");
            ValidateModel(profile, model);
            await _modelStore.ActivateAsync(model, cancellationToken);
            _models[profileId] = model;
            _metrics[profileId].SetPendingModel(null);
            return ToVersionInfo(model, isActive: true);
        }
        finally
        {
            profileLock.Release();
        }
    }

    public IReadOnlyList<PlcMlProfileStatus> GetStatus() =>
        _options.Profiles.Select(profile =>
        {
            var runtime = _metrics[profile.ProfileId];
            var windows = _windowEngine.GetMetrics(profile.ProfileId);
            _models.TryGetValue(profile.ProfileId, out var model);
            var drift = runtime.LatestDrift;
            return new PlcMlProfileStatus
            {
                ProfileId = profile.ProfileId,
                Enabled = profile.Enabled,
                DeploymentMode = profile.DeploymentMode,
                CanaryPercentage = profile.CanaryPercentage,
                ActiveModelVersion = model?.Version,
                TrainingWindowCount = runtime.TrainingCount,
                CompletedWindows = windows.Completed,
                DroppedIncompleteWindows = windows.Dropped,
                Predictions = runtime.Predictions,
                AnomalyObservations = runtime.AnomalyObservations,
                Raised = runtime.Raised,
                Recovered = runtime.Recovered,
                ShadowRaised = runtime.ShadowRaised,
                ActiveRaised = runtime.ActiveRaised,
                ActiveAnomalies = CountActiveAnomalies(profile.ProfileId),
                TrackedWindows = windows.Tracked,
                TrackedInferenceStates = _states.Values.Count(state =>
                    string.Equals(state.Profile.ProfileId, profile.ProfileId, StringComparison.Ordinal)),
                DriftStatus = drift?.Status ?? PlcMlDriftStatus.Unknown,
                DriftRatio = drift?.DriftRatio ?? 0,
                DriftSampleCount = drift?.SampleCount ?? 0,
                Failures = runtime.Failures,
                LastError = runtime.LastError
            };
        }).ToList();

    private async Task ProcessVectorAsync(
        PlcFeatureVector vector,
        CancellationToken cancellationToken)
    {
        if (!_profiles.TryGetValue(vector.ProfileId, out var profile) || !profile.Enabled) return;
        var runtime = _metrics[profile.ProfileId];

        try
        {
            if (profile.CollectTrainingData && runtime.TrainingCount < profile.MaximumTrainingWindows)
            {
                await _trainingStore.AppendAsync(vector, profile.MaximumTrainingWindows, cancellationToken);
                runtime.IncrementTrainingCount();
            }

            if (profile.DeploymentMode == PlcMlDeploymentMode.Disabled) return;

            AnomalyTransition? transition = null;
            PlcMlDriftSnapshot? driftSnapshot = null;
            var profileLock = _profileLocks[profile.ProfileId];
            await profileLock.WaitAsync(cancellationToken);
            try
            {
                if (!_models.TryGetValue(profile.ProfileId, out var model)) return;
                if (!vector.FeatureNames.SequenceEqual(model.FeatureNames, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Profile {profile.ProfileId} 特征顺序与活动模型不一致。");

                var score = IsolationForest.Score(model, vector.Values);
                var observationThreshold = Math.Max(model.DecisionThreshold, profile.ObserveThreshold);
                var formalThreshold = Math.Max(observationThreshold, profile.WarningThreshold);
                var observed = score >= observationThreshold;
                var abnormal = score >= formalThreshold;
                runtime.IncrementPredictions();
                if (observed) runtime.IncrementAnomalyObservations();
                driftSnapshot = runtime.RecordScore(profile, model, score, vector.WindowEndUtc);

                var stateKey = $"{profile.ProfileId}|{vector.PlcName}|{vector.DeviceId}";
                var state = _states.GetOrAdd(stateKey, _ => new InferenceState(profile));
                var routeToActive = ShouldRouteToActiveLifecycle(profile, vector.DeviceId);
                lock (state.Gate)
                {
                    transition = ApplyPredictionLocked(
                        state,
                        vector,
                        model,
                        score,
                        formalThreshold,
                        abnormal,
                        routeToActive);
                    state.LastUpdatedUtc = vector.WindowEndUtc;
                }
            }
            finally
            {
                profileLock.Release();
            }

            if (driftSnapshot is not null)
                await _governanceStore.SaveDriftSnapshotAsync(driftSnapshot, cancellationToken);

            if (transition is null) return;
            if (transition.IsDetected)
            {
                runtime.IncrementRaised(transition.RoutedToActiveLifecycle);
                await _governanceStore.UpsertCandidateAsync(
                    ToCandidate(profile, transition.Record, transition.RoutedToActiveLifecycle),
                    cancellationToken);
                if (transition.RoutedToActiveLifecycle)
                {
                    await _eventBus.PublishAsync(
                        new PlcAnomalyDetectedEvent { Anomaly = transition.Record },
                        cancellationToken);
                }
            }
            else
            {
                runtime.IncrementRecovered();
                await _governanceStore.RecoverCandidateAsync(
                    transition.Record.AnomalyId,
                    transition.Record.EndTimeUtc ?? transition.Record.LastSeenUtc,
                    cancellationToken);
                if (transition.RoutedToActiveLifecycle)
                {
                    await _eventBus.PublishAsync(
                        new PlcAnomalyRecoveredEvent { Anomaly = transition.Record },
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.RecordFailure(ex);
        }
    }

    private static AnomalyTransition? ApplyPredictionLocked(
        InferenceState state,
        PlcFeatureVector vector,
        PlcIsolationForestModel model,
        double score,
        double threshold,
        bool abnormal,
        bool routeToActive)
    {
        if (abnormal)
        {
            state.NormalCount = 0;
            state.AbnormalCount++;
            state.FirstAbnormalUtc ??= vector.WindowStartUtc;
            if (state.Active is null && state.AbnormalCount >= state.Profile.ConsecutiveAbnormalCount)
            {
                var record = CreateRecord(
                    state.Profile,
                    vector,
                    model,
                    score,
                    threshold,
                    state.FirstAbnormalUtc.Value);
                state.Active = record;
                state.RoutedToActiveLifecycle = routeToActive;
                return new AnomalyTransition(true, record, routeToActive);
            }

            if (state.Active is not null)
            {
                state.Active = state.Active with
                {
                    Score = Math.Max(state.Active.Score, score),
                    ActualValue = score,
                    ExpectedValue = threshold,
                    LastSeenUtc = vector.WindowEndUtc,
                    Severity = ResolveSeverity(state.Profile, score),
                    Reason = BuildExplanation(vector, model, score, threshold),
                    ContextJson = BuildContext(vector, model, score, threshold)
                };
            }
            return null;
        }

        state.AbnormalCount = 0;
        state.FirstAbnormalUtc = null;
        if (state.Active is null)
        {
            state.NormalCount = 0;
            return null;
        }

        state.NormalCount++;
        if (state.NormalCount < state.Profile.ConsecutiveRecoveryCount) return null;
        var recovered = state.Active.Recover(vector.WindowEndUtc);
        var routed = state.RoutedToActiveLifecycle;
        state.Active = null;
        state.RoutedToActiveLifecycle = false;
        state.NormalCount = 0;
        return new AnomalyTransition(false, recovered, routed);
    }

    private static PlcAnomalyRecord CreateRecord(
        PlcMlProfile profile,
        PlcFeatureVector vector,
        PlcIsolationForestModel model,
        double score,
        double threshold,
        DateTime startUtc)
    {
        var anomalyKey = $"ML|{profile.ProfileId}|{vector.PlcName}|{vector.DeviceId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(anomalyKey));
        return new PlcAnomalyRecord
        {
            AnomalyId = Guid.NewGuid().ToString("N"),
            AnomalyKey = anomalyKey,
            AlarmCode = $"PLC_ML_{Convert.ToHexString(hash.AsSpan(0, 8))}",
            RuleId = $"ML:{profile.ProfileId}",
            Type = PlcAnomalyType.MachineLearning,
            Severity = ResolveSeverity(profile, score),
            Status = PlcAnomalyLifecycleStatus.Active,
            PlcName = vector.PlcName,
            DbBlock = 0,
            DeviceId = vector.DeviceId,
            SignalName = "ML_FEATURE_WINDOW",
            DetectorName = "IsolationForest",
            ModelVersion = model.Version,
            Score = score,
            ActualValue = score,
            ExpectedValue = threshold,
            LowerBound = 0,
            UpperBound = threshold,
            StartTimeUtc = startUtc,
            LastSeenUtc = vector.WindowEndUtc,
            Reason = BuildExplanation(vector, model, score, threshold),
            RaiseAlarm = profile.RaiseAlarm,
            ContextJson = BuildContext(vector, model, score, threshold)
        };
    }

    private static PlcMlCandidateRecord ToCandidate(
        PlcMlProfile profile,
        PlcAnomalyRecord record,
        bool routedToActive) => new()
    {
        CandidateId = record.AnomalyId,
        CandidateKey = record.AnomalyKey,
        ProfileId = profile.ProfileId,
        ModelVersion = record.ModelVersion,
        DeploymentMode = profile.DeploymentMode,
        RoutedToActiveLifecycle = routedToActive,
        PlcName = record.PlcName,
        DeviceId = record.DeviceId,
        WindowStartUtc = record.StartTimeUtc,
        WindowEndUtc = record.LastSeenUtc,
        Score = record.Score,
        Threshold = record.ExpectedValue ?? 0,
        Explanation = record.Reason,
        ContextJson = record.ContextJson,
        IsActive = true,
        DetectedUtc = record.StartTimeUtc,
        ReviewDecision = PlcMlReviewDecision.Unreviewed
    };

    private PlcMlProfile GetProfile(string profileId) =>
        _profiles.TryGetValue(profileId, out var profile)
            ? profile
            : throw new KeyNotFoundException($"未找到 PLC ML Profile：{profileId}。");

    private void EnsureNoActiveAnomalies(string profileId, string operation)
    {
        var active = CountActiveAnomalies(profileId);
        if (active > 0)
            throw new InvalidOperationException(
                $"Profile {profileId} 当前有 {active} 个活动异常候选，不能{operation}。请先完成恢复。");
    }

    private int CountActiveAnomalies(string profileId) =>
        _states.Values.Count(state =>
            state.Active is not null &&
            string.Equals(state.Profile.ProfileId, profileId, StringComparison.Ordinal));

    private static bool ShouldRouteToActiveLifecycle(PlcMlProfile profile, string deviceId)
    {
        if (profile.DeploymentMode == PlcMlDeploymentMode.Active) return true;
        if (profile.DeploymentMode != PlcMlDeploymentMode.Canary || profile.CanaryPercentage <= 0) return false;
        if (profile.CanaryPercentage >= 100) return true;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.ProfileId}|{deviceId}"));
        var bucket = ((hash[0] << 8) | hash[1]) % 100;
        return bucket < profile.CanaryPercentage;
    }

    private static void ValidateModel(PlcMlProfile profile, PlcIsolationForestModel model)
    {
        if (!string.Equals(profile.ProfileId, model.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("模型 ProfileId 与配置不一致。");
        if (string.IsNullOrWhiteSpace(model.Version) || model.Trees.Length == 0)
            throw new InvalidOperationException("模型版本或森林为空。");
        var expected = BuildExpectedFeatureNames(profile);
        if (!model.FeatureNames.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"模型特征与 Profile {profile.ProfileId} 不一致。Expected={string.Join(',', expected)}; Actual={string.Join(',', model.FeatureNames)}");
        if (model.Means.Length != expected.Length || model.StandardDeviations.Length != expected.Length)
            throw new InvalidOperationException("模型标准化参数维度与特征不一致。");
    }

    private static string[] BuildExpectedFeatureNames(PlcMlProfile profile)
    {
        var result = new List<string>(profile.Signals.Count * 8);
        foreach (var signal in profile.Signals)
        {
            if (signal.Kind == PlcMlSignalKind.Numeric)
            {
                result.Add($"{signal.Name}.mean");
                result.Add($"{signal.Name}.stddev");
                result.Add($"{signal.Name}.min");
                result.Add($"{signal.Name}.max");
                result.Add($"{signal.Name}.last");
                result.Add($"{signal.Name}.slope");
                result.Add($"{signal.Name}.range");
                result.Add($"{signal.Name}.samplesPerSecond");
            }
            else
            {
                result.Add($"{signal.Name}.trueRatio");
                result.Add($"{signal.Name}.transitions");
                result.Add($"{signal.Name}.last");
                result.Add($"{signal.Name}.samplesPerSecond");
            }
        }
        return result.ToArray();
    }

    private static PlcMlModelVersionInfo ToVersionInfo(PlcIsolationForestModel model, bool isActive) => new()
    {
        ProfileId = model.ProfileId,
        Version = model.Version,
        CreatedUtc = model.CreatedUtc,
        TrainingSampleCount = model.TrainingSampleCount,
        CalibrationSampleCount = model.CalibrationSampleCount,
        TreeCount = model.Trees.Length,
        DecisionThreshold = model.DecisionThreshold,
        IsActive = isActive
    };

    private static PlcAnomalySeverity ResolveSeverity(PlcMlProfile profile, double score)
    {
        if (score >= profile.AlarmThreshold) return PlcAnomalySeverity.Error;
        if (score >= profile.WarningThreshold && profile.Severity < PlcAnomalySeverity.Warning)
            return PlcAnomalySeverity.Warning;
        return profile.Severity;
    }

    private static string BuildExplanation(
        PlcFeatureVector vector,
        PlcIsolationForestModel model,
        double score,
        double threshold)
    {
        var normalized = IsolationForest.Normalize(vector.Values, model.Means, model.StandardDeviations);
        var important = normalized
            .Select((value, index) => new
            {
                Name = model.FeatureNames[index],
                Z = Math.Abs(value),
                Raw = vector.Values[index]
            })
            .OrderByDescending(static item => item.Z)
            .Take(3)
            .Select(static item => $"{item.Name}={item.Raw:G6}(偏离{item.Z:F2}σ)");
        return $"Isolation Forest 异常分数 {score:F4}，正式阈值 {threshold:F4}；主要偏离：{string.Join("，", important)}";
    }

    private static string BuildContext(
        PlcFeatureVector vector,
        PlcIsolationForestModel model,
        double score,
        double threshold) => PlcAnomalyRecord.SerializeContext(new
        {
            vector.ProfileId,
            vector.PlcName,
            vector.DeviceId,
            vector.WindowStartUtc,
            vector.WindowEndUtc,
            vector.SourceSampleCount,
            model.Version,
            score,
            threshold,
            features = vector.FeatureNames.Zip(
                vector.Values,
                static (name, value) => new { name, value })
        });

    private sealed class InferenceState
    {
        public InferenceState(PlcMlProfile profile) => Profile = profile;
        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public int AbnormalCount { get; set; }
        public int NormalCount { get; set; }
        public DateTime? FirstAbnormalUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public PlcAnomalyRecord? Active { get; set; }
        public bool RoutedToActiveLifecycle { get; set; }
    }

    private sealed class ProfileRuntimeMetrics
    {
        private readonly object _scoreGate = new();
        private readonly Queue<double> _scores = new();
        private int _trainingCount;
        private long _predictions;
        private long _anomalyObservations;
        private long _raised;
        private long _recovered;
        private long _shadowRaised;
        private long _activeRaised;
        private long _failures;
        private string? _lastError;
        private string? _pendingModelVersion;
        private PlcMlDriftSnapshot? _latestDrift;
        private PlcMlDriftSnapshot? _pendingDrift;
        private DateTime _lastDriftUtc;

        public int TrainingCount => Volatile.Read(ref _trainingCount);
        public long Predictions => Interlocked.Read(ref _predictions);
        public long AnomalyObservations => Interlocked.Read(ref _anomalyObservations);
        public long Raised => Interlocked.Read(ref _raised);
        public long Recovered => Interlocked.Read(ref _recovered);
        public long ShadowRaised => Interlocked.Read(ref _shadowRaised);
        public long ActiveRaised => Interlocked.Read(ref _activeRaised);
        public long Failures => Interlocked.Read(ref _failures);
        public string? LastError => Volatile.Read(ref _lastError);
        public string? PendingModelVersion => Volatile.Read(ref _pendingModelVersion);
        public PlcMlDriftSnapshot? LatestDrift
        {
            get
            {
                lock (_scoreGate) return _latestDrift;
            }
        }

        public void SetTrainingCount(int count) => Volatile.Write(ref _trainingCount, count);
        public void IncrementTrainingCount() => Interlocked.Increment(ref _trainingCount);
        public void IncrementPredictions() => Interlocked.Increment(ref _predictions);
        public void IncrementAnomalyObservations() => Interlocked.Increment(ref _anomalyObservations);
        public void IncrementRaised(bool active)
        {
            Interlocked.Increment(ref _raised);
            if (active) Interlocked.Increment(ref _activeRaised);
            else Interlocked.Increment(ref _shadowRaised);
        }
        public void IncrementRecovered() => Interlocked.Increment(ref _recovered);
        public void SetPendingModel(string? version) => Volatile.Write(ref _pendingModelVersion, version);

        public PlcMlDriftSnapshot? RecordScore(
            PlcMlProfile profile,
            PlcIsolationForestModel model,
            double score,
            DateTime utcNow)
        {
            lock (_scoreGate)
            {
                _scores.Enqueue(score);
                while (_scores.Count > profile.DriftWindowSize) _scores.Dequeue();
                if (_scores.Count < profile.MinimumDriftSamples) return null;
                if (_lastDriftUtc != default &&
                    utcNow - _lastDriftUtc < TimeSpan.FromSeconds(profile.DriftSnapshotIntervalSeconds))
                    return null;

                var values = _scores.OrderBy(static value => value).ToArray();
                var mean = values.Average();
                var p95Index = Math.Clamp((int)Math.Ceiling(values.Length * 0.95) - 1, 0, values.Length - 1);
                var p95 = values[p95Index];
                var baselineMean = model.CalibrationMeanScore > 0
                    ? model.CalibrationMeanScore
                    : Math.Max(model.DecisionThreshold * 0.8, 0.01);
                var baselineP95 = model.CalibrationP95Score > 0
                    ? model.CalibrationP95Score
                    : Math.Max(model.DecisionThreshold, 0.01);
                var meanRatio = Math.Max(0, (mean - baselineMean) / baselineMean);
                var p95Ratio = Math.Max(0, (p95 - baselineP95) / baselineP95);
                var ratio = Math.Max(meanRatio, p95Ratio);
                var status = ratio >= profile.DriftCriticalRatio
                    ? PlcMlDriftStatus.Critical
                    : ratio >= profile.DriftWarningRatio
                        ? PlcMlDriftStatus.Warning
                        : PlcMlDriftStatus.Stable;
                var snapshot = new PlcMlDriftSnapshot
                {
                    SnapshotId = $"{profile.ProfileId}|{model.Version}|{utcNow:yyyyMMddHHmmss}",
                    ProfileId = profile.ProfileId,
                    ModelVersion = model.Version,
                    CalculatedUtc = utcNow,
                    SampleCount = values.Length,
                    MeanScore = mean,
                    P95Score = p95,
                    BaselineMeanScore = baselineMean,
                    BaselineP95Score = baselineP95,
                    DriftRatio = ratio,
                    Status = status
                };
                _latestDrift = snapshot;
                _pendingDrift = snapshot;
                _lastDriftUtc = utcNow;
                return snapshot;
            }
        }

        public PlcMlDriftSnapshot? TakePendingDriftSnapshot()
        {
            lock (_scoreGate)
            {
                var value = _pendingDrift;
                _pendingDrift = null;
                return value;
            }
        }

        public void RecordFailure(Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    private sealed record AnomalyTransition(
        bool IsDetected,
        PlcAnomalyRecord Record,
        bool RoutedToActiveLifecycle);
}
