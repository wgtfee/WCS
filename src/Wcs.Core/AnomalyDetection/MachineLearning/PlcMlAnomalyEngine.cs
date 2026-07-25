namespace Wcs.Core.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// PLC 机器学习异常引擎：窗口特征、训练数据采集、Isolation Forest 训练、版本加载与在线生命周期。
/// </summary>
public sealed class PlcMlAnomalyEngine : IPlcMlAnomalyEngine
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly PlcFeatureWindowEngine _windowEngine;
    private readonly IPlcMlModelStore _modelStore;
    private readonly IPlcMlTrainingStore _trainingStore;
    private readonly IEventBus _eventBus;
    private readonly IReadOnlyDictionary<string, PlcMlProfile> _profiles;
    private readonly ConcurrentDictionary<string, PlcIsolationForestModel> _models = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InferenceState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProfileRuntimeMetrics> _metrics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _trainingLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private int _initialized;

    public PlcMlAnomalyEngine(
        PlcMlAnomalyOptions options,
        PlcFeatureWindowEngine windowEngine,
        IPlcMlModelStore modelStore,
        IPlcMlTrainingStore trainingStore,
        IEventBus eventBus)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windowEngine = windowEngine ?? throw new ArgumentNullException(nameof(windowEngine));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _trainingStore = trainingStore ?? throw new ArgumentNullException(nameof(trainingStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _profiles = options.Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);

        foreach (var profile in options.Profiles)
        {
            _metrics.TryAdd(profile.ProfileId, new ProfileRuntimeMetrics());
            _trainingLocks.TryAdd(profile.ProfileId, new SemaphoreSlim(1, 1));
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
                    var model = await _modelStore.LoadActiveAsync(profile.ProfileId, cancellationToken);
                    if (model is not null) _models[profile.ProfileId] = model;
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
            if (!profile.Enabled || !profile.AutoTrain || _models.ContainsKey(profile.ProfileId)) continue;
            if (_metrics[profile.ProfileId].TrainingCount < profile.MinimumTrainingWindows) continue;
            try
            {
                await TrainAsync(profile.ProfileId, cancellationToken);
            }
            catch (Exception ex)
            {
                _metrics[profile.ProfileId].RecordFailure(ex);
            }
        }
    }

    public async Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
            throw new KeyNotFoundException($"未找到 PLC ML Profile：{profileId}。");

        var trainingLock = _trainingLocks[profileId];
        await trainingLock.WaitAsync(cancellationToken);
        try
        {
            var vectors = await _trainingStore.ReadAsync(
                profileId,
                profile.MaximumTrainingWindows,
                cancellationToken);
            var model = IsolationForest.Train(profile, vectors, DateTime.UtcNow);
            await _modelStore.SaveAndActivateAsync(model, cancellationToken);
            _models[profileId] = model;
            _metrics[profileId].SetTrainingCount(vectors.Count);
            return new PlcMlTrainingResult
            {
                ProfileId = profileId,
                ModelVersion = model.Version,
                TrainingSampleCount = model.TrainingSampleCount,
                TreeCount = model.Trees.Length,
                DecisionThreshold = model.DecisionThreshold,
                CreatedUtc = model.CreatedUtc
            };
        }
        finally
        {
            trainingLock.Release();
        }
    }

    public IReadOnlyList<PlcMlProfileStatus> GetStatus() =>
        _options.Profiles.Select(profile =>
        {
            var runtime = _metrics[profile.ProfileId];
            var windows = _windowEngine.GetMetrics(profile.ProfileId);
            _models.TryGetValue(profile.ProfileId, out var model);
            return new PlcMlProfileStatus
            {
                ProfileId = profile.ProfileId,
                Enabled = profile.Enabled,
                ActiveModelVersion = model?.Version,
                TrainingWindowCount = runtime.TrainingCount,
                CompletedWindows = windows.Completed,
                DroppedIncompleteWindows = windows.Dropped,
                Predictions = runtime.Predictions,
                AnomalyObservations = runtime.AnomalyObservations,
                Raised = runtime.Raised,
                Recovered = runtime.Recovered,
                ActiveAnomalies = _states.Values.Count(state =>
                    state.Active is not null &&
                    string.Equals(state.Profile.ProfileId, profile.ProfileId, StringComparison.Ordinal)),
                TrackedWindows = windows.Tracked,
                TrackedInferenceStates = _states.Values.Count(state =>
                    string.Equals(state.Profile.ProfileId, profile.ProfileId, StringComparison.Ordinal)),
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

            if (!_models.TryGetValue(profile.ProfileId, out var model)) return;
            if (!vector.FeatureNames.SequenceEqual(model.FeatureNames, StringComparer.Ordinal))
                throw new InvalidOperationException($"Profile {profile.ProfileId} 特征顺序与活动模型不一致。");

            var score = IsolationForest.Score(model, vector.Values);
            var threshold = Math.Max(model.DecisionThreshold, profile.ObserveThreshold);
            var abnormal = score >= threshold;
            runtime.IncrementPredictions();
            if (abnormal) runtime.IncrementAnomalyObservations();

            var stateKey = $"{profile.ProfileId}|{vector.PlcName}|{vector.DeviceId}";
            var state = _states.GetOrAdd(stateKey, _ => new InferenceState(profile));
            AnomalyTransition? transition;
            lock (state.Gate)
            {
                transition = ApplyPredictionLocked(state, vector, model, score, threshold, abnormal);
                state.LastUpdatedUtc = vector.WindowEndUtc;
            }

            if (transition is null) return;
            if (transition.IsDetected)
            {
                runtime.IncrementRaised();
                await _eventBus.PublishAsync(
                    new PlcAnomalyDetectedEvent { Anomaly = transition.Record },
                    cancellationToken);
            }
            else
            {
                runtime.IncrementRecovered();
                await _eventBus.PublishAsync(
                    new PlcAnomalyRecoveredEvent { Anomaly = transition.Record },
                    cancellationToken);
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
        bool abnormal)
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
                return new AnomalyTransition(true, record);
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
        state.Active = null;
        state.NormalCount = 0;
        return new AnomalyTransition(false, recovered);
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
        return $"Isolation Forest 异常分数 {score:F4}，阈值 {threshold:F4}；主要偏离：{string.Join("，", important)}";
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
    }

    private sealed class ProfileRuntimeMetrics
    {
        private int _trainingCount;
        private long _predictions;
        private long _anomalyObservations;
        private long _raised;
        private long _recovered;
        private long _failures;
        private string? _lastError;

        public int TrainingCount => Volatile.Read(ref _trainingCount);
        public long Predictions => Interlocked.Read(ref _predictions);
        public long AnomalyObservations => Interlocked.Read(ref _anomalyObservations);
        public long Raised => Interlocked.Read(ref _raised);
        public long Recovered => Interlocked.Read(ref _recovered);
        public long Failures => Interlocked.Read(ref _failures);
        public string? LastError => Volatile.Read(ref _lastError);
        public void SetTrainingCount(int count) => Volatile.Write(ref _trainingCount, count);
        public void IncrementTrainingCount() => Interlocked.Increment(ref _trainingCount);
        public void IncrementPredictions() => Interlocked.Increment(ref _predictions);
        public void IncrementAnomalyObservations() => Interlocked.Increment(ref _anomalyObservations);
        public void IncrementRaised() => Interlocked.Increment(ref _raised);
        public void IncrementRecovered() => Interlocked.Increment(ref _recovered);
        public void RecordFailure(Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    private sealed record AnomalyTransition(bool IsDetected, PlcAnomalyRecord Record);
}
