namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMlTrainingContaminationTests
{
    [Fact]
    public async Task Active_model_blocks_online_training_collection_by_default()
    {
        var setup = CreateSetup(collectWhileActive: false);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await PublishWindowAsync(setup.Engine, start, 20, 21, 22);

        Assert.Equal(0, setup.TrainingStore.AppendCount);
        Assert.Equal(100, Assert.Single(setup.Engine.GetStatus()).TrainingWindowCount);
    }

    [Fact]
    public async Task Explicit_recollection_mode_allows_online_training_collection()
    {
        var setup = CreateSetup(collectWhileActive: true);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await PublishWindowAsync(setup.Engine, start, 5.0, 5.1, 5.2);

        Assert.Equal(1, setup.TrainingStore.AppendCount);
        Assert.Equal(101, Assert.Single(setup.Engine.GetStatus()).TrainingWindowCount);
    }

    private static Setup CreateSetup(bool collectWhileActive)
    {
        var profile = new PlcMlProfile
        {
            ProfileId = "TRAINING-GUARD",
            Enabled = true,
            PlcPattern = "PLC-1",
            DevicePattern = "CV01",
            WindowSeconds = 1,
            MinimumSamplesPerSignal = 3,
            CollectTrainingData = true,
            CollectTrainingDataWhileModelActive = collectWhileActive,
            MaximumTrainingWindows = 1000,
            DeploymentMode = PlcMlDeploymentMode.Shadow,
            RequireModelApproval = true,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            Signals = new List<PlcMlSignalDefinition>
            {
                new() { Name = "Current", Pattern = "CV01_Current", Kind = PlcMlSignalKind.Numeric }
            }
        };
        var model = new PlcIsolationForestModel
        {
            ProfileId = profile.ProfileId,
            Version = "approved-model",
            CreatedUtc = DateTime.UtcNow,
            FeatureNames = new[]
            {
                "Current.mean", "Current.stddev", "Current.min", "Current.max",
                "Current.last", "Current.slope", "Current.range", "Current.samplesPerSecond"
            },
            Means = new double[8],
            StandardDeviations = Enumerable.Repeat(1.0, 8).ToArray(),
            Trees = new[] { new IsolationForestNode { SampleCount = 2 } },
            TrainingSampleCount = 100,
            CalibrationSampleCount = 20,
            SubsampleSize = 2,
            DecisionThreshold = 0.99,
            CalibrationMeanScore = 0.4,
            CalibrationP95Score = 0.5
        };
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var trainingStore = new TrainingStore();
        var engine = new PlcMlAnomalyEngine(
            options,
            new PlcFeatureWindowEngine(options),
            new ModelStore(model),
            trainingStore,
            new GovernanceStore(model),
            new EventBus());
        engine.InitializeAsync().GetAwaiter().GetResult();
        return new Setup(engine, trainingStore);
    }

    private static async Task PublishWindowAsync(
        PlcMlAnomalyEngine engine,
        DateTime start,
        double first,
        double second,
        double third)
    {
        await engine.ProcessAsync(Sample(first, start.AddMilliseconds(100)));
        await engine.ProcessAsync(Sample(second, start.AddMilliseconds(400)));
        await engine.ProcessAsync(Sample(third, start.AddMilliseconds(700)));
        await engine.MaintenanceAsync(start.AddSeconds(1.1));
    }

    private static PlcAnomalySample Sample(double value, DateTime timestampUtc) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestampUtc,
        PlcName = "PLC-1",
        DbBlock = 1,
        DeviceId = "CV01",
        SignalName = "CV01_Current",
        NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NumericValue = value
    };

    private sealed record Setup(PlcMlAnomalyEngine Engine, TrainingStore TrainingStore);

    private sealed class TrainingStore : IPlcMlTrainingStore
    {
        public int AppendCount { get; private set; }
        public Task<int> CountAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(100);
        public Task AppendAsync(PlcFeatureVector vector, int maximumWindows, CancellationToken cancellationToken = default)
        {
            AppendCount++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(string profileId, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(Array.Empty<PlcFeatureVector>());
        public Task<PlcMlDatasetInfo> CreateDatasetAsync(string profileId, int maximumWindows, string createdBy, string? description, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlDatasetInfo>>(Array.Empty<PlcMlDatasetInfo>());
        public Task<IReadOnlyList<PlcFeatureVector>> ReadDatasetAsync(string profileId, string datasetVersion, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(Array.Empty<PlcFeatureVector>());
    }

    private sealed class ModelStore : IPlcMlModelStore
    {
        private readonly PlcIsolationForestModel _model;
        public ModelStore(PlcIsolationForestModel model) => _model = model;
        public Task<PlcIsolationForestModel?> LoadActiveAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcIsolationForestModel?>(_model);
        public Task<PlcIsolationForestModel?> LoadVersionAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcIsolationForestModel?>(_model.Version == version ? _model : null);
        public Task SaveVersionAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAndActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlcMlModelVersionInfo>> ListAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelVersionInfo>>(Array.Empty<PlcMlModelVersionInfo>());
    }

    private sealed class GovernanceStore : IPlcMlGovernanceStore
    {
        private readonly PlcIsolationForestModel _model;
        public GovernanceStore(PlcIsolationForestModel model) => _model = model;
        public Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecoverCandidateAsync(string candidateId, DateTime recoveredUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(string? profileId, PlcMlReviewDecision? decision, int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlCandidateRecord>>(Array.Empty<PlcMlCandidateRecord>());
        public Task<PlcMlCandidateRecord> ReviewCandidateAsync(string candidateId, PlcMlReviewDecision decision, string reviewedBy, string? comment, DateTime reviewedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlModelGovernanceInfo?> GetModelAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcMlModelGovernanceInfo?>(new PlcMlModelGovernanceInfo
            {
                GovernanceId = $"{profileId}|{version}",
                ProfileId = profileId,
                ModelVersion = version,
                ApprovalStatus = PlcMlApprovalStatus.Approved,
                RequestedUtc = _model.CreatedUtc,
                RequestedBy = "trainer",
                TrainingSampleCount = _model.TrainingSampleCount,
                CalibrationSampleCount = _model.CalibrationSampleCount,
                DecisionThreshold = _model.DecisionThreshold
            });
        public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelGovernanceInfo>>(Array.Empty<PlcMlModelGovernanceInfo>());
        public Task<PlcMlModelGovernanceInfo> DecideModelAsync(string profileId, string version, PlcMlApprovalStatus status, string actor, string? comment, DateTime decidedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsModelApprovedAsync(string profileId, string version, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<PlcMlDriftSnapshot?>(null);
        public Task<PlcMlEvaluationSummary> GetEvaluationAsync(string profileId, string? modelVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlcMlEvaluationSummary { ProfileId = profileId });
    }
}
