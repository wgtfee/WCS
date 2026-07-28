namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning.Adapters;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// Decorates the mature Isolation Forest engine with explicitly configured local external-model profiles.
/// External profiles never enter the legacy engine, so one feature window is evaluated by exactly one model path.
/// </summary>
public sealed class PluggablePlcMlAnomalyEngine :
    IPlcMlAnomalyEngine,
    IPlcMlExternalRuntimeStatusProvider,
    IDisposable
{
    private readonly PlcMlAnomalyEngine _legacy;
    private readonly PlcMlAnomalyOptions _allOptions;
    private readonly PlcMlPluggableRuntimeOptions _runtimeOptions;
    private readonly IPlcMlExternalModelStore _modelStore;
    private readonly PlcMlModelAdapterRegistry _adapterRegistry;
    private readonly IPlcMlGovernanceStore _governanceStore;
    private readonly IEventBus _eventBus;
    private readonly PlcFeatureWindowEngine _windowEngine;
    private readonly IReadOnlyDictionary<string, ExternalProfileRuntime> _profiles;
    private readonly ConcurrentDictionary<string, ExternalInferenceState> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private int _initialized;
    private int _disposed;

    public PluggablePlcMlAnomalyEngine(
        PlcMlAnomalyEngine legacy,
        PlcMlAnomalyOptions allOptions,
        PlcMlPluggableRuntimeOptions runtimeOptions,
        IPlcMlExternalModelStore modelStore,
        PlcMlModelAdapterRegistry adapterRegistry,
        IPlcMlGovernanceStore governanceStore,
        IEventBus eventBus)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _allOptions = allOptions ?? throw new ArgumentNullException(nameof(allOptions));
        _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        _governanceStore = governanceStore ?? throw new ArgumentNullException(nameof(governanceStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

        var configured = new Dictionary<string, ExternalProfileRuntime>(StringComparer.Ordinal);
        if (_runtimeOptions.Enabled)
        {
            var allProfiles = _allOptions.Profiles.ToDictionary(item => item.ProfileId, StringComparer.Ordinal);
            foreach (var mapping in _runtimeOptions.Profiles)
            {
                mapping.ProfileId = mapping.ProfileId?.Trim() ?? string.Empty;
                if (mapping.ProfileId.Length == 0)
                    throw new InvalidOperationException("PluggableRuntime profile requires ProfileId.");
                if (!configured.TryAdd(
                        mapping.ProfileId,
                        new ExternalProfileRuntime(
                            allProfiles.TryGetValue(mapping.ProfileId, out var profile)
                                ? profile
                                : throw new InvalidOperationException(
                                    $"PluggableRuntime profile was not found in MachineLearning.Profiles: {mapping.ProfileId}."),
                            mapping)))
                    throw new InvalidOperationException($"Duplicate PluggableRuntime ProfileId: {mapping.ProfileId}.");
                if (!Enum.IsDefined(mapping.AdapterKind))
                    throw new InvalidOperationException(
                        $"Unsupported adapter kind for profile {mapping.ProfileId}: {mapping.AdapterKind}.");
                if (!allProfiles[mapping.ProfileId].Enabled)
                    throw new InvalidOperationException(
                        $"PluggableRuntime profile must be enabled: {mapping.ProfileId}.");
                if (allProfiles[mapping.ProfileId].CollectTrainingData || allProfiles[mapping.ProfileId].AutoTrain)
                    throw new InvalidOperationException(
                        $"External profile {mapping.ProfileId} cannot collect online training data or auto-train.");
            }
        }
        _profiles = configured;

        _runtimeOptions.MaximumTrackedWindows = Math.Clamp(
            _runtimeOptions.MaximumTrackedWindows,
            1,
            1_000_000);
        _runtimeOptions.InactiveStateRetentionSeconds = Math.Clamp(
            _runtimeOptions.InactiveStateRetentionSeconds,
            1,
            86_400);
        _windowEngine = new PlcFeatureWindowEngine(new PlcMlAnomalyOptions
        {
            Enabled = _runtimeOptions.Enabled,
            MaximumTrackedWindows = _runtimeOptions.MaximumTrackedWindows,
            Profiles = _profiles.Values.Select(static item => item.Profile).ToList()
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) == 1) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized == 1) return;
            await _legacy.InitializeAsync(cancellationToken);
            if (_runtimeOptions.Enabled)
            {
                foreach (var runtime in _profiles.Values)
                    await TryLoadActiveAsync(runtime, cancellationToken);
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
        ThrowIfDisposed();
        await _legacy.ProcessAsync(sample, cancellationToken);
        if (!_runtimeOptions.Enabled) return;
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken);
        foreach (var vector in _windowEngine.Process(sample))
            await ProcessExternalVectorAsync(vector, cancellationToken);
    }

    public async Task MaintenanceAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _legacy.MaintenanceAsync(utcNow, cancellationToken);
        if (!_runtimeOptions.Enabled) return;
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken);

        foreach (var vector in _windowEngine.FlushExpired(utcNow))
            await ProcessExternalVectorAsync(vector, cancellationToken);

        var cutoff = utcNow.AddSeconds(-_runtimeOptions.InactiveStateRetentionSeconds);
        foreach (var pair in _states)
        {
            var removable = false;
            lock (pair.Value.Gate)
                removable = pair.Value.Active is null && pair.Value.LastUpdatedUtc < cutoff;
            if (removable)
                ((ICollection<KeyValuePair<string, ExternalInferenceState>>)_states).Remove(pair);
        }
    }

    public Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        TrainAsync(profileId, datasetVersion: null, requestedBy: null, cancellationToken);

    public Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        string? datasetVersion,
        string? requestedBy,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_profiles.ContainsKey(profileId))
            throw new InvalidOperationException(
                $"External profile {profileId} accepts only approved offline artifacts and cannot train inside WCS.");
        return _legacy.TrainAsync(profileId, datasetVersion, requestedBy, cancellationToken);
    }

    public async Task<IReadOnlyList<PlcMlModelVersionInfo>> ListModelsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_profiles.TryGetValue(profileId, out var runtime))
            return await _legacy.ListModelsAsync(profileId, cancellationToken);

        var manifests = await _modelStore.ListAsync(profileId, cancellationToken);
        string? activeVersion;
        lock (runtime.Gate) activeVersion = runtime.Loaded?.Runtime.Manifest.Version;
        return manifests.Select(manifest => new PlcMlModelVersionInfo
        {
            ProfileId = manifest.ProfileId,
            Version = manifest.Version,
            CreatedUtc = manifest.CreatedUtc,
            TrainingSampleCount = 0,
            CalibrationSampleCount = 0,
            TreeCount = 0,
            DecisionThreshold = manifest.DecisionThreshold,
            IsActive = string.Equals(activeVersion, manifest.Version, StringComparison.Ordinal)
        }).ToArray();
    }

    public async Task<PlcMlModelVersionInfo> ActivateModelAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_profiles.TryGetValue(profileId, out var profileRuntime))
            return await _legacy.ActivateModelAsync(profileId, version, cancellationToken);
        if (CountActiveAnomalies(profileId) > 0)
            throw new InvalidOperationException(
                $"External profile {profileId} has active anomalies and cannot change model version.");

        var artifact = await _modelStore.LoadVersionAsync(profileId, version, cancellationToken)
            ?? throw new KeyNotFoundException($"External model was not found: {profileId}/{version}.");
        var loaded = await LoadRuntimeAsync(profileRuntime, artifact, cancellationToken);
        await _modelStore.ActivateAsync(profileId, version, cancellationToken);
        SwapLoadedRuntime(profileRuntime, loaded);
        profileRuntime.ClearFailure();
        return new PlcMlModelVersionInfo
        {
            ProfileId = artifact.Manifest.ProfileId,
            Version = artifact.Manifest.Version,
            CreatedUtc = artifact.Manifest.CreatedUtc,
            TrainingSampleCount = 0,
            CalibrationSampleCount = 0,
            TreeCount = 0,
            DecisionThreshold = artifact.Manifest.DecisionThreshold,
            IsActive = true
        };
    }

    public IReadOnlyList<PlcMlProfileStatus> GetStatus()
    {
        ThrowIfDisposed();
        var legacy = _legacy.GetStatus().ToDictionary(item => item.ProfileId, StringComparer.Ordinal);
        var result = new List<PlcMlProfileStatus>(_allOptions.Profiles.Count);
        foreach (var profile in _allOptions.Profiles)
        {
            if (!_profiles.TryGetValue(profile.ProfileId, out var external))
            {
                if (legacy.TryGetValue(profile.ProfileId, out var status)) result.Add(status);
                continue;
            }

            var windows = _windowEngine.GetMetrics(profile.ProfileId);
            var snapshot = external.Snapshot(
                windows,
                CountActiveAnomalies(profile.ProfileId),
                CountTrackedStates(profile.ProfileId));
            result.Add(new PlcMlProfileStatus
            {
                ProfileId = profile.ProfileId,
                Enabled = profile.Enabled,
                DeploymentMode = profile.DeploymentMode,
                CanaryPercentage = profile.CanaryPercentage,
                ActiveModelVersion = snapshot.ActiveModelVersion,
                TrainingWindowCount = 0,
                CompletedWindows = snapshot.CompletedWindows,
                DroppedIncompleteWindows = snapshot.DroppedIncompleteWindows,
                Predictions = snapshot.Predictions,
                AnomalyObservations = snapshot.AnomalyObservations,
                Raised = snapshot.Raised,
                Recovered = snapshot.Recovered,
                ShadowRaised = snapshot.ShadowRaised,
                ActiveRaised = snapshot.ActiveRaised,
                ActiveAnomalies = snapshot.ActiveAnomalies,
                TrackedWindows = snapshot.TrackedWindows,
                TrackedInferenceStates = snapshot.TrackedInferenceStates,
                DriftStatus = PlcMlDriftStatus.Unknown,
                Failures = snapshot.Failures,
                LastError = snapshot.LastError
            });
        }
        return result;
    }

    public IReadOnlyList<PlcMlExternalRuntimeStatus> GetExternalRuntimeStatus()
    {
        ThrowIfDisposed();
        return _profiles.Values.Select(runtime => runtime.Snapshot(
                _windowEngine.GetMetrics(runtime.Profile.ProfileId),
                CountActiveAnomalies(runtime.Profile.ProfileId),
                CountTrackedStates(runtime.Profile.ProfileId)))
            .OrderBy(static item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var profile in _profiles.Values)
        {
            LoadedExternalModel? loaded;
            lock (profile.Gate)
            {
                loaded = profile.Loaded;
                profile.Loaded = null;
            }
            loaded?.Runtime.Dispose();
        }
        _initializeLock.Dispose();
    }

    private async Task TryLoadActiveAsync(
        ExternalProfileRuntime profileRuntime,
        CancellationToken cancellationToken)
    {
        try
        {
            var artifact = await _modelStore.LoadActiveAsync(
                profileRuntime.Profile.ProfileId,
                cancellationToken);
            if (artifact is null)
            {
                if (profileRuntime.Mapping.Required)
                    throw new InvalidOperationException(
                        $"Required external model is not active for profile {profileRuntime.Profile.ProfileId}.");
                return;
            }
            var loaded = await LoadRuntimeAsync(profileRuntime, artifact, cancellationToken);
            SwapLoadedRuntime(profileRuntime, loaded);
            profileRuntime.ClearFailure();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            profileRuntime.RecordFailure(exception);
        }
    }

    private async Task<LoadedExternalModel> LoadRuntimeAsync(
        ExternalProfileRuntime profileRuntime,
        PlcMlModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        PlcMlModelManifestValidator.Validate(profileRuntime.Profile, artifact.Manifest);
        PlcMlFeatureSchema.ValidateManifest(profileRuntime.Profile, artifact.Manifest);
        if (artifact.Manifest.AdapterKind != profileRuntime.Mapping.AdapterKind)
            throw new InvalidOperationException(
                $"Active model adapter kind {artifact.Manifest.AdapterKind} does not match configured kind {profileRuntime.Mapping.AdapterKind} for {profileRuntime.Profile.ProfileId}.");
        var adapter = _adapterRegistry.Resolve(artifact.Manifest.AdapterKind);
        var runtime = await adapter.LoadAsync(profileRuntime.Profile, artifact, cancellationToken);
        return new LoadedExternalModel(
            runtime,
            PlcMlModelManifestValidator.ComputeManifestHash(artifact.Manifest),
            DateTime.UtcNow);
    }

    private static void SwapLoadedRuntime(
        ExternalProfileRuntime profileRuntime,
        LoadedExternalModel loaded)
    {
        LoadedExternalModel? previous;
        lock (profileRuntime.Gate)
        {
            previous = profileRuntime.Loaded;
            profileRuntime.Loaded = loaded;
        }
        previous?.Runtime.Dispose();
    }

    private async Task ProcessExternalVectorAsync(
        PlcFeatureVector vector,
        CancellationToken cancellationToken)
    {
        if (!_profiles.TryGetValue(vector.ProfileId, out var profileRuntime) ||
            profileRuntime.Profile.DeploymentMode == PlcMlDeploymentMode.Disabled)
            return;
        try
        {
            PlcMlAdapterPrediction prediction;
            LoadedExternalModel loaded;
            lock (profileRuntime.Gate)
            {
                loaded = profileRuntime.Loaded ?? throw new InvalidOperationException(
                    $"No active external model is loaded for profile {vector.ProfileId}.");
                prediction = loaded.Runtime.Predict(vector);
            }

            profileRuntime.IncrementPredictions();
            var observationThreshold = Math.Max(
                prediction.DecisionThreshold,
                profileRuntime.Profile.ObserveThreshold);
            var formalThreshold = Math.Max(
                observationThreshold,
                profileRuntime.Profile.WarningThreshold);
            var observed = prediction.Score >= observationThreshold;
            var abnormal = prediction.Score >= formalThreshold;
            if (observed) profileRuntime.IncrementAnomalyObservations();

            var stateKey = $"{vector.ProfileId}|{vector.PlcName}|{vector.DeviceId}";
            var state = _states.GetOrAdd(
                stateKey,
                _ => new ExternalInferenceState(profileRuntime.Profile));
            ExternalTransition? transition;
            var routeToActive = ShouldRouteToActiveLifecycle(
                profileRuntime.Profile,
                vector.DeviceId);
            lock (state.Gate)
            {
                transition = ApplyPredictionLocked(
                    state,
                    vector,
                    prediction,
                    loaded,
                    formalThreshold,
                    abnormal,
                    routeToActive);
                state.LastUpdatedUtc = vector.WindowEndUtc;
            }

            if (transition is null) return;
            if (transition.IsDetected)
            {
                profileRuntime.IncrementRaised(transition.RoutedToActiveLifecycle);
                await _governanceStore.UpsertCandidateAsync(
                    ToCandidate(profileRuntime.Profile, transition.Record, transition.RoutedToActiveLifecycle),
                    cancellationToken);
                if (transition.RoutedToActiveLifecycle)
                    await _eventBus.PublishAsync(
                        new PlcAnomalyDetectedEvent { Anomaly = transition.Record },
                        cancellationToken);
            }
            else
            {
                profileRuntime.IncrementRecovered();
                await _governanceStore.RecoverCandidateAsync(
                    transition.Record.AnomalyId,
                    transition.Record.EndTimeUtc ?? transition.Record.LastSeenUtc,
                    cancellationToken);
                if (transition.RoutedToActiveLifecycle)
                    await _eventBus.PublishAsync(
                        new PlcAnomalyRecoveredEvent { Anomaly = transition.Record },
                        cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            profileRuntime.RecordFailure(exception);
        }
    }

    private static ExternalTransition? ApplyPredictionLocked(
        ExternalInferenceState state,
        PlcFeatureVector vector,
        PlcMlAdapterPrediction prediction,
        LoadedExternalModel loaded,
        double threshold,
        bool abnormal,
        bool routeToActive)
    {
        if (abnormal)
        {
            state.NormalCount = 0;
            state.AbnormalCount++;
            state.FirstAbnormalUtc ??= vector.WindowStartUtc;
            if (state.Active is null &&
                state.AbnormalCount >= state.Profile.ConsecutiveAbnormalCount)
            {
                var record = CreateRecord(
                    state.Profile,
                    vector,
                    prediction,
                    loaded,
                    threshold,
                    state.FirstAbnormalUtc.Value);
                state.Active = record;
                state.RoutedToActiveLifecycle = routeToActive;
                return new ExternalTransition(true, record, routeToActive);
            }

            if (state.Active is not null)
            {
                state.Active = state.Active with
                {
                    Score = Math.Max(state.Active.Score, prediction.Score),
                    ActualValue = prediction.Score,
                    ExpectedValue = threshold,
                    LastSeenUtc = vector.WindowEndUtc,
                    Severity = ResolveSeverity(state.Profile, prediction.Score),
                    Reason = prediction.Explanation,
                    ContextJson = BuildContext(vector, prediction, loaded, threshold)
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
        return new ExternalTransition(false, recovered, routed);
    }

    private static PlcAnomalyRecord CreateRecord(
        PlcMlProfile profile,
        PlcFeatureVector vector,
        PlcMlAdapterPrediction prediction,
        LoadedExternalModel loaded,
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
            Severity = ResolveSeverity(profile, prediction.Score),
            Status = PlcAnomalyLifecycleStatus.Active,
            PlcName = vector.PlcName,
            DbBlock = 0,
            DeviceId = vector.DeviceId,
            SignalName = "ML_FEATURE_WINDOW",
            DetectorName = prediction.DetectorName,
            ModelVersion = prediction.ModelVersion,
            Score = prediction.Score,
            ActualValue = prediction.Score,
            ExpectedValue = threshold,
            LowerBound = 0,
            UpperBound = threshold,
            StartTimeUtc = startUtc,
            LastSeenUtc = vector.WindowEndUtc,
            Reason = prediction.Explanation,
            RaiseAlarm = profile.RaiseAlarm,
            ContextJson = BuildContext(vector, prediction, loaded, threshold)
        };
    }

    private static string BuildContext(
        PlcFeatureVector vector,
        PlcMlAdapterPrediction prediction,
        LoadedExternalModel loaded,
        double threshold) => PlcAnomalyRecord.SerializeContext(new
        {
            vector.ProfileId,
            vector.PlcName,
            vector.DeviceId,
            vector.WindowStartUtc,
            vector.WindowEndUtc,
            vector.SourceSampleCount,
            prediction.ModelVersion,
            prediction.AdapterKind,
            prediction.AdapterId,
            prediction.DetectorName,
            prediction.Score,
            threshold,
            loaded.ManifestHash,
            loaded.Runtime.Manifest.ArtifactSha256,
            loaded.Runtime.Manifest.Source,
            loaded.Runtime.Manifest.ApprovedBy,
            loaded.Runtime.Manifest.ApprovedAtUtc,
            features = vector.FeatureNames.Zip(
                vector.Values,
                static (name, value) => new { name, value })
        });

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

    private static PlcAnomalySeverity ResolveSeverity(
        PlcMlProfile profile,
        double score)
    {
        if (score >= profile.AlarmThreshold) return PlcAnomalySeverity.Error;
        if (score >= profile.WarningThreshold && profile.Severity < PlcAnomalySeverity.Warning)
            return PlcAnomalySeverity.Warning;
        return profile.Severity;
    }

    private static bool ShouldRouteToActiveLifecycle(
        PlcMlProfile profile,
        string deviceId)
    {
        if (profile.DeploymentMode == PlcMlDeploymentMode.Active) return true;
        if (profile.DeploymentMode != PlcMlDeploymentMode.Canary ||
            profile.CanaryPercentage <= 0) return false;
        if (profile.CanaryPercentage >= 100) return true;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.ProfileId}|{deviceId}"));
        var bucket = ((hash[0] << 8) | hash[1]) % 100;
        return bucket < profile.CanaryPercentage;
    }

    private int CountActiveAnomalies(string profileId) =>
        _states.Values.Count(state =>
            state.Active is not null &&
            string.Equals(state.Profile.ProfileId, profileId, StringComparison.Ordinal));

    private int CountTrackedStates(string profileId) =>
        _states.Values.Count(state =>
            string.Equals(state.Profile.ProfileId, profileId, StringComparison.Ordinal));

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ExternalInferenceState
    {
        public ExternalInferenceState(PlcMlProfile profile) => Profile = profile;
        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public int AbnormalCount { get; set; }
        public int NormalCount { get; set; }
        public DateTime? FirstAbnormalUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public PlcAnomalyRecord? Active { get; set; }
        public bool RoutedToActiveLifecycle { get; set; }
    }

    private sealed class ExternalProfileRuntime
    {
        private long _predictions;
        private long _observations;
        private long _raised;
        private long _recovered;
        private long _shadowRaised;
        private long _activeRaised;
        private long _failures;
        private string? _lastError;

        public ExternalProfileRuntime(
            PlcMlProfile profile,
            PlcMlExternalProfileOptions mapping)
        {
            Profile = profile;
            Mapping = mapping;
        }

        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public PlcMlExternalProfileOptions Mapping { get; }
        public LoadedExternalModel? Loaded { get; set; }

        public void IncrementPredictions() => Interlocked.Increment(ref _predictions);
        public void IncrementAnomalyObservations() => Interlocked.Increment(ref _observations);
        public void IncrementRecovered() => Interlocked.Increment(ref _recovered);

        public void IncrementRaised(bool active)
        {
            Interlocked.Increment(ref _raised);
            if (active) Interlocked.Increment(ref _activeRaised);
            else Interlocked.Increment(ref _shadowRaised);
        }

        public void RecordFailure(Exception exception)
        {
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, exception.Message);
        }

        public void ClearFailure() => Volatile.Write(ref _lastError, null);

        public PlcMlExternalRuntimeStatus Snapshot(
            WindowMetricsSnapshot windows,
            int activeAnomalies,
            int trackedStates)
        {
            LoadedExternalModel? loaded;
            lock (Gate) loaded = Loaded;
            return new PlcMlExternalRuntimeStatus
            {
                ProfileId = Profile.ProfileId,
                RuntimeEnabled = true,
                Required = Mapping.Required,
                ConfiguredAdapterKind = Mapping.AdapterKind,
                ActiveAdapterId = loaded?.Runtime.Manifest.AdapterId,
                ActiveModelVersion = loaded?.Runtime.Manifest.Version,
                ManifestHash = loaded?.ManifestHash,
                ArtifactSha256 = loaded?.Runtime.Manifest.ArtifactSha256,
                Predictions = Interlocked.Read(ref _predictions),
                AnomalyObservations = Interlocked.Read(ref _observations),
                Raised = Interlocked.Read(ref _raised),
                Recovered = Interlocked.Read(ref _recovered),
                ShadowRaised = Interlocked.Read(ref _shadowRaised),
                ActiveRaised = Interlocked.Read(ref _activeRaised),
                Failures = Interlocked.Read(ref _failures),
                ActiveAnomalies = activeAnomalies,
                TrackedInferenceStates = trackedStates,
                CompletedWindows = checked((int)Math.Min(int.MaxValue, windows.Completed)),
                DroppedIncompleteWindows = checked((int)Math.Min(int.MaxValue, windows.Dropped)),
                TrackedWindows = windows.Tracked,
                LoadedUtc = loaded?.LoadedUtc,
                LastError = Volatile.Read(ref _lastError)
            };
        }
    }

    private sealed record LoadedExternalModel(
        IPlcMlModelRuntime Runtime,
        string ManifestHash,
        DateTime LoadedUtc);

    private sealed record ExternalTransition(
        bool IsDetected,
        PlcAnomalyRecord Record,
        bool RoutedToActiveLifecycle);
}
