namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMachineLearningTests
{
    [Fact]
    public void Isolation_forest_separates_obvious_outliers_from_normal_windows()
    {
        var profile = CreateProfile();
        profile.MinimumTrainingWindows = 100;
        profile.TreeCount = 120;
        profile.SampleSize = 128;
        profile.Contamination = 0.05;
        var vectors = Enumerable.Range(0, 500).Select(NormalTrainingVector).ToArray();

        var model = IsolationForest.Train(profile, vectors, DateTime.UtcNow);
        var normalScore = IsolationForest.Score(model, VectorFromSamples(5.0, 5.02, 5.04, 999).Values);
        var anomalyScore = IsolationForest.Score(model, VectorFromSamples(20, 21, 22, 1000).Values);

        Assert.True(normalScore < model.DecisionThreshold, $"normal={normalScore}, threshold={model.DecisionThreshold}");
        Assert.True(anomalyScore >= model.DecisionThreshold, $"anomaly={anomalyScore}, threshold={model.DecisionThreshold}");
        Assert.True(anomalyScore > normalScore + 0.05, $"normal={normalScore}, anomaly={anomalyScore}");
        Assert.Equal(100, model.CalibrationSampleCount);
    }

    [Fact]
    public void Feature_window_emits_deterministic_numeric_features()
    {
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            Profiles = new List<PlcMlProfile> { CreateProfile() }
        };
        var engine = new PlcFeatureWindowEngine(options);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        engine.Process(Sample(4, start.AddMilliseconds(100)));
        engine.Process(Sample(5, start.AddMilliseconds(300)));
        engine.Process(Sample(6, start.AddMilliseconds(700)));
        var vector = Assert.Single(engine.FlushExpired(start.AddSeconds(1.1)));

        Assert.Equal("CV-MOTOR", vector.ProfileId);
        Assert.Equal(8, vector.Values.Length);
        Assert.Equal("Current.mean", vector.FeatureNames[0]);
        Assert.Equal(5.0, vector.Values[0], 6);
        Assert.Equal(4.0, vector.Values[2], 6);
        Assert.Equal(6.0, vector.Values[3], 6);
        Assert.Equal(6.0, vector.Values[4], 6);
        Assert.Equal(2.0, vector.Values[6], 6);
        Assert.Equal(3, vector.SourceSampleCount);
    }

    [Fact]
    public async Task Ml_engine_publishes_one_lifecycle_and_recovers_after_normal_window()
    {
        var setup = CreateEngineSetup();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await PublishAnomalyWindow(setup.Engine, start);
        var anomaly = Assert.Single(setup.Detected).Anomaly;
        Assert.Equal(PlcAnomalyType.MachineLearning, anomaly.Type);
        Assert.Equal("IsolationForest", anomaly.DetectorName);
        Assert.Equal(setup.Model.Version, anomaly.ModelVersion);
        Assert.Single(setup.Governance.Candidates);

        await PublishNormalWindow(setup.Engine, start.AddSeconds(1));
        Assert.Single(setup.Recovered);
        Assert.False(Assert.Single(setup.Governance.Candidates).IsActive);
        var status = Assert.Single(setup.Engine.GetStatus());
        Assert.Equal(1, status.Raised);
        Assert.Equal(1, status.Recovered);
        Assert.Equal(1, status.ActiveRaised);
        Assert.Equal(0, status.ShadowRaised);
        Assert.Equal(0, status.ActiveAnomalies);
        Assert.Equal(0, status.Failures);
    }

    [Fact]
    public async Task Shadow_mode_records_candidate_without_publishing_formal_lifecycle()
    {
        var setup = CreateEngineSetup(PlcMlDeploymentMode.Shadow);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await PublishAnomalyWindow(setup.Engine, start);

        Assert.Empty(setup.Detected);
        var candidate = Assert.Single(setup.Governance.Candidates);
        Assert.False(candidate.RoutedToActiveLifecycle);
        Assert.Equal(PlcMlDeploymentMode.Shadow, candidate.DeploymentMode);
        var status = Assert.Single(setup.Engine.GetStatus());
        Assert.Equal(1, status.ShadowRaised);
        Assert.Equal(0, status.ActiveRaised);

        await PublishNormalWindow(setup.Engine, start.AddSeconds(1));
        Assert.Empty(setup.Recovered);
        Assert.False(Assert.Single(setup.Governance.Candidates).IsActive);
    }

    [Fact]
    public async Task Model_activation_is_blocked_during_active_anomaly_and_allowed_after_recovery()
    {
        var setup = CreateEngineSetup();
        var second = CloneModel(setup.Model, "rollback-version", setup.Model.CreatedUtc.AddMinutes(-1));
        setup.ModelStore.Add(second);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await PublishAnomalyWindow(setup.Engine, start);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Engine.ActivateModelAsync("CV-MOTOR", second.Version));
        Assert.Contains("活动异常", error.Message);

        await PublishNormalWindow(setup.Engine, start.AddSeconds(1));
        var activated = await setup.Engine.ActivateModelAsync("CV-MOTOR", second.Version);
        Assert.True(activated.IsActive);
        Assert.Equal(second.Version, activated.Version);
        Assert.Equal(second.Version, Assert.Single(setup.Engine.GetStatus()).ActiveModelVersion);

        var versions = await setup.Engine.ListModelsAsync("CV-MOTOR");
        Assert.Equal(2, versions.Count);
        Assert.Single(versions, item => item.IsActive && item.Version == second.Version);
    }

    [Fact]
    public async Task Model_requires_approval_before_activation()
    {
        var profile = CreateProfile();
        profile.RequireModelApproval = true;
        profile.DeploymentMode = PlcMlDeploymentMode.Shadow;
        var training = Enumerable.Range(0, 500).Select(NormalTrainingVector).ToArray();
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var modelStore = new MemoryModelStore();
        var governance = new MemoryGovernanceStore();
        var engine = new PlcMlAnomalyEngine(
            options,
            new PlcFeatureWindowEngine(options),
            modelStore,
            new MemoryTrainingStore(training),
            governance,
            new EventBus());
        await engine.InitializeAsync();

        var result = await engine.TrainAsync("CV-MOTOR", null, "trainer");
        Assert.False(result.Activated);
        Assert.Equal(PlcMlApprovalStatus.Pending, result.ApprovalStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ActivateModelAsync("CV-MOTOR", result.ModelVersion));

        await governance.DecideModelAsync(
            "CV-MOTOR",
            result.ModelVersion,
            PlcMlApprovalStatus.Approved,
            "reviewer",
            "approved",
            DateTime.UtcNow);
        var activated = await engine.ActivateModelAsync("CV-MOTOR", result.ModelVersion);
        Assert.True(activated.IsActive);
    }

    private static EngineSetup CreateEngineSetup(
        PlcMlDeploymentMode deploymentMode = PlcMlDeploymentMode.Active)
    {
        var profile = CreateProfile();
        profile.DeploymentMode = deploymentMode;
        profile.RequireModelApproval = false;
        profile.ConsecutiveAbnormalCount = 1;
        profile.ConsecutiveRecoveryCount = 1;
        profile.MinimumTrainingWindows = 100;
        profile.Contamination = 0.05;
        profile.ObserveThreshold = 0.50;
        profile.WarningThreshold = 0.55;
        var training = Enumerable.Range(0, 500).Select(NormalTrainingVector).ToArray();
        var model = IsolationForest.Train(profile, training, DateTime.UtcNow);
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            InactiveInferenceStateRetentionSeconds = 10,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var eventBus = new EventBus();
        var detected = new ConcurrentBag<PlcAnomalyDetectedEvent>();
        var recovered = new ConcurrentBag<PlcAnomalyRecoveredEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
            return Task.CompletedTask;
        });
        eventBus.Subscribe<PlcAnomalyRecoveredEvent>((evt, _) =>
        {
            recovered.Add(evt);
            return Task.CompletedTask;
        });
        var modelStore = new MemoryModelStore(model);
        var governance = new MemoryGovernanceStore();
        var engine = new PlcMlAnomalyEngine(
            options,
            new PlcFeatureWindowEngine(options),
            modelStore,
            new MemoryTrainingStore(training),
            governance,
            eventBus);
        engine.InitializeAsync().GetAwaiter().GetResult();
        return new EngineSetup(engine, modelStore, governance, model, detected, recovered);
    }

    private static async Task PublishAnomalyWindow(PlcMlAnomalyEngine engine, DateTime start)
    {
        await engine.ProcessAsync(Sample(20, start.AddMilliseconds(100)));
        await engine.ProcessAsync(Sample(21, start.AddMilliseconds(400)));
        await engine.ProcessAsync(Sample(22, start.AddMilliseconds(700)));
        await engine.MaintenanceAsync(start.AddSeconds(1.1));
    }

    private static async Task PublishNormalWindow(PlcMlAnomalyEngine engine, DateTime start)
    {
        await engine.ProcessAsync(Sample(5.0, start.AddMilliseconds(100)));
        await engine.ProcessAsync(Sample(5.02, start.AddMilliseconds(400)));
        await engine.ProcessAsync(Sample(5.04, start.AddMilliseconds(700)));
        await engine.MaintenanceAsync(start.AddSeconds(1.1));
    }

    private static PlcMlProfile CreateProfile() => new()
    {
        ProfileId = "CV-MOTOR",
        PlcPattern = "PLC-TEST",
        DevicePattern = "CV01",
        WindowSeconds = 1,
        MinimumSamplesPerSignal = 3,
        MinimumTrainingWindows = 100,
        MaximumTrainingWindows = 10_000,
        TreeCount = 100,
        SampleSize = 128,
        Contamination = 0.05,
        ObserveThreshold = 0.50,
        WarningThreshold = 0.55,
        AlarmThreshold = 0.85,
        ConsecutiveAbnormalCount = 1,
        ConsecutiveRecoveryCount = 1,
        DeploymentMode = PlcMlDeploymentMode.Active,
        RequireModelApproval = false,
        RaiseAlarm = false,
        DriftWindowSize = 20,
        MinimumDriftSamples = 10,
        DriftSnapshotIntervalSeconds = 1,
        Signals = new List<PlcMlSignalDefinition>
        {
            new() { Name = "Current", Pattern = "CV01_Current", Kind = PlcMlSignalKind.Numeric }
        }
    };

    private static PlcFeatureVector NormalTrainingVector(int index)
    {
        var baseline = 5 + Math.Sin(index * 0.17) * 0.25;
        return VectorFromSamples(
            baseline + Math.Sin(index * 0.13) * 0.10,
            baseline + Math.Sin(index * 0.13 + 0.19) * 0.10,
            baseline + Math.Sin(index * 0.13 + 0.38) * 0.10,
            index);
    }

    private static PlcFeatureVector VectorFromSamples(double first, double second, double last, int index)
    {
        var values = new[] { first, second, last };
        var mean = values.Average();
        var variance = values.Select(value => (value - mean) * (value - mean)).Average();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index);
        return new PlcFeatureVector
        {
            ProfileId = "CV-MOTOR",
            PlcName = "PLC-TEST",
            DeviceId = "CV01",
            WindowStartUtc = start,
            WindowEndUtc = start.AddSeconds(1),
            FeatureNames = new[]
            {
                "Current.mean", "Current.stddev", "Current.min", "Current.max",
                "Current.last", "Current.slope", "Current.range", "Current.samplesPerSecond"
            },
            Values = new[]
            {
                mean, Math.Sqrt(variance), values.Min(), values.Max(), last,
                (last - first) / 0.6, values.Max() - values.Min(), 3.0
            },
            SourceSampleCount = 3
        };
    }

    private static PlcAnomalySample Sample(double value, DateTime timestampUtc) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestampUtc,
        PlcName = "PLC-TEST",
        DbBlock = 1,
        DeviceId = "CV01",
        SignalName = "CV01_Current",
        NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NumericValue = value
    };

    private static PlcIsolationForestModel CloneModel(
        PlcIsolationForestModel source,
        string version,
        DateTime createdUtc) => new()
    {
        ProfileId = source.ProfileId,
        Version = version,
        CreatedUtc = createdUtc,
        FeatureNames = source.FeatureNames.ToArray(),
        Means = source.Means.ToArray(),
        StandardDeviations = source.StandardDeviations.ToArray(),
        Trees = source.Trees,
        TrainingSampleCount = source.TrainingSampleCount,
        CalibrationSampleCount = source.CalibrationSampleCount,
        SubsampleSize = source.SubsampleSize,
        DecisionThreshold = source.DecisionThreshold,
        Contamination = source.Contamination,
        CalibrationMeanScore = source.CalibrationMeanScore,
        CalibrationP95Score = source.CalibrationP95Score
    };

    private sealed record EngineSetup(
        PlcMlAnomalyEngine Engine,
        MemoryModelStore ModelStore,
        MemoryGovernanceStore Governance,
        PlcIsolationForestModel Model,
        ConcurrentBag<PlcAnomalyDetectedEvent> Detected,
        ConcurrentBag<PlcAnomalyRecoveredEvent> Recovered);

    private sealed class MemoryModelStore : IPlcMlModelStore
    {
        private readonly Dictionary<string, PlcIsolationForestModel> _models = new(StringComparer.Ordinal);
        private PlcIsolationForestModel? _active;

        public MemoryModelStore(PlcIsolationForestModel? model = null)
        {
            if (model is not null)
            {
                Add(model);
                _active = model;
            }
        }

        public void Add(PlcIsolationForestModel model) => _models[model.Version] = model;

        public Task<PlcIsolationForestModel?> LoadActiveAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_active?.ProfileId == profileId ? _active : null);

        public Task<PlcIsolationForestModel?> LoadVersionAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(_models.TryGetValue(version, out var model) && model.ProfileId == profileId ? model : null);

        public Task SaveVersionAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default)
        {
            Add(model);
            return Task.CompletedTask;
        }

        public Task ActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default)
        {
            Add(model);
            _active = model;
            return Task.CompletedTask;
        }

        public Task SaveAndActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default) =>
            ActivateAsync(model, cancellationToken);

        public Task<IReadOnlyList<PlcMlModelVersionInfo>> ListAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelVersionInfo>>(_models.Values
                .Where(model => model.ProfileId == profileId)
                .Select(model => new PlcMlModelVersionInfo
                {
                    ProfileId = model.ProfileId,
                    Version = model.Version,
                    CreatedUtc = model.CreatedUtc,
                    TrainingSampleCount = model.TrainingSampleCount,
                    CalibrationSampleCount = model.CalibrationSampleCount,
                    TreeCount = model.Trees.Length,
                    DecisionThreshold = model.DecisionThreshold,
                    IsActive = _active?.Version == model.Version
                })
                .OrderByDescending(item => item.CreatedUtc)
                .ToList());
    }

    private sealed class MemoryTrainingStore : IPlcMlTrainingStore
    {
        private readonly List<PlcFeatureVector> _vectors;
        private readonly Dictionary<string, (PlcMlDatasetInfo Info, PlcFeatureVector[] Vectors)> _datasets = new(StringComparer.Ordinal);

        public MemoryTrainingStore(IEnumerable<PlcFeatureVector> vectors) => _vectors = vectors.ToList();
        public Task<int> CountAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(_vectors.Count);
        public Task AppendAsync(PlcFeatureVector vector, int maximumWindows, CancellationToken cancellationToken = default)
        {
            if (_vectors.Count < maximumWindows) _vectors.Add(vector);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(string profileId, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(_vectors.TakeLast(maximumWindows).ToArray());
        public Task<PlcMlDatasetInfo> CreateDatasetAsync(string profileId, int maximumWindows, string createdBy, string? description, CancellationToken cancellationToken = default)
        {
            var vectors = _vectors.TakeLast(maximumWindows).ToArray();
            var info = new PlcMlDatasetInfo
            {
                ProfileId = profileId,
                Version = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
                WindowCount = vectors.Length,
                FeatureHash = "memory",
                CreatedBy = createdBy,
                Description = description
            };
            _datasets[info.Version] = (info, vectors);
            return Task.FromResult(info);
        }
        public Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlDatasetInfo>>(_datasets.Values.Select(value => value.Info).ToArray());
        public Task<IReadOnlyList<PlcFeatureVector>> ReadDatasetAsync(string profileId, string datasetVersion, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(_datasets[datasetVersion].Vectors.TakeLast(maximumWindows).ToArray());
    }

    private sealed class MemoryGovernanceStore : IPlcMlGovernanceStore
    {
        private readonly Dictionary<string, PlcMlCandidateRecord> _candidates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlcMlModelGovernanceInfo> _models = new(StringComparer.Ordinal);
        private PlcMlDriftSnapshot? _drift;
        public IReadOnlyCollection<PlcMlCandidateRecord> Candidates => _candidates.Values;

        public Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default)
        {
            _candidates[candidate.CandidateId] = candidate;
            return Task.CompletedTask;
        }
        public Task RecoverCandidateAsync(string candidateId, DateTime recoveredUtc, CancellationToken cancellationToken = default)
        {
            _candidates[candidateId] = _candidates[candidateId] with { IsActive = false, RecoveredUtc = recoveredUtc };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(string? profileId, PlcMlReviewDecision? decision, int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlCandidateRecord>>(_candidates.Values.Take(maximumCount).ToArray());
        public Task<PlcMlCandidateRecord> ReviewCandidateAsync(string candidateId, PlcMlReviewDecision decision, string reviewedBy, string? comment, DateTime reviewedUtc, CancellationToken cancellationToken = default)
        {
            var value = _candidates[candidateId] with
            {
                ReviewDecision = decision,
                ReviewedBy = reviewedBy,
                ReviewedUtc = reviewedUtc,
                ReviewComment = comment
            };
            _candidates[candidateId] = value;
            return Task.FromResult(value);
        }
        public Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default)
        {
            _models[model.GovernanceId] = model;
            return Task.CompletedTask;
        }
        public Task<PlcMlModelGovernanceInfo?> GetModelAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(_models.GetValueOrDefault($"{profileId}|{version}"));
        public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelGovernanceInfo>>(_models.Values.Where(value => value.ProfileId == profileId).ToArray());
        public Task<PlcMlModelGovernanceInfo> DecideModelAsync(string profileId, string version, PlcMlApprovalStatus status, string actor, string? comment, DateTime decidedUtc, CancellationToken cancellationToken = default)
        {
            var key = $"{profileId}|{version}";
            var value = _models[key] with
            {
                ApprovalStatus = status,
                DecidedBy = actor,
                DecidedUtc = decidedUtc,
                DecisionComment = comment
            };
            _models[key] = value;
            return Task.FromResult(value);
        }
        public Task<bool> IsModelApprovedAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(_models.TryGetValue($"{profileId}|{version}", out var value) && value.ApprovalStatus == PlcMlApprovalStatus.Approved);
        public Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _drift = snapshot;
            return Task.CompletedTask;
        }
        public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(_drift);
        public Task<PlcMlEvaluationSummary> GetEvaluationAsync(string profileId, string? modelVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlcMlEvaluationSummary { ProfileId = profileId });
    }
}
