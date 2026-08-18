namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class PlcMlGovernanceTests
{
    [Fact]
    public async Task Training_requester_cannot_approve_own_model()
    {
        var profile = CreateProfile();
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            ManagementApiEnabled = true,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var governance = new GovernanceStore(new PlcMlModelGovernanceInfo
        {
            GovernanceId = "CV-MOTOR|model-1",
            ProfileId = "CV-MOTOR",
            ModelVersion = "model-1",
            ApprovalStatus = PlcMlApprovalStatus.Pending,
            RequestedUtc = DateTime.UtcNow,
            RequestedBy = "trainer-a",
            TrainingSampleCount = 500,
            CalibrationSampleCount = 100,
            DecisionThreshold = 0.6
        });
        var engine = new EngineStub();
        var service = new PlcMlGovernanceService(
            options,
            new TrainingStoreStub(),
            governance,
            engine);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApproveModelAsync(
                "CV-MOTOR",
                "model-1",
                "TRAINER-A",
                "self approval",
                activate: true));

        Assert.Contains("不能审批自己", error.Message);
        Assert.Equal(0, engine.ActivationCount);
        Assert.Equal(PlcMlApprovalStatus.Pending, governance.Model.ApprovalStatus);
    }

    [Fact]
    public async Task Independent_reviewer_can_approve_and_activate_model()
    {
        var profile = CreateProfile();
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            ManagementApiEnabled = true,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var governance = new GovernanceStore(new PlcMlModelGovernanceInfo
        {
            GovernanceId = "CV-MOTOR|model-1",
            ProfileId = "CV-MOTOR",
            ModelVersion = "model-1",
            ApprovalStatus = PlcMlApprovalStatus.Pending,
            RequestedUtc = DateTime.UtcNow,
            RequestedBy = "trainer-a",
            TrainingSampleCount = 500,
            CalibrationSampleCount = 100,
            DecisionThreshold = 0.6
        });
        var engine = new EngineStub();
        var service = new PlcMlGovernanceService(
            options,
            new TrainingStoreStub(),
            governance,
            engine);

        var approved = await service.ApproveModelAsync(
            "CV-MOTOR",
            "model-1",
            "reviewer-b",
            "independent approval",
            activate: true);

        Assert.Equal(PlcMlApprovalStatus.Approved, approved.ApprovalStatus);
        Assert.Equal("reviewer-b", approved.DecidedBy);
        Assert.Equal(1, engine.ActivationCount);
    }

    private static PlcMlProfile CreateProfile() => new()
    {
        ProfileId = "CV-MOTOR",
        Enabled = true,
        RequireModelApproval = true,
        Signals = new List<PlcMlSignalDefinition>
        {
            new() { Name = "Current", Pattern = "*_Current", Kind = PlcMlSignalKind.Numeric }
        }
    };

    private sealed class EngineStub : IPlcMlAnomalyEngine
    {
        public int ActivationCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask ProcessAsync(PlcAnomalySample sample, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task MaintenanceAsync(DateTime utcNow, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlTrainingResult> TrainAsync(string profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlcMlTrainingResult> TrainAsync(string profileId, string? datasetVersion, string? requestedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlcMlModelVersionInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelVersionInfo>>(Array.Empty<PlcMlModelVersionInfo>());
        public Task<PlcMlModelVersionInfo> ActivateModelAsync(string profileId, string version, CancellationToken cancellationToken = default)
        {
            ActivationCount++;
            return Task.FromResult(new PlcMlModelVersionInfo
            {
                ProfileId = profileId,
                Version = version,
                CreatedUtc = DateTime.UtcNow,
                TrainingSampleCount = 500,
                CalibrationSampleCount = 100,
                TreeCount = 100,
                DecisionThreshold = 0.6,
                IsActive = true
            });
        }
        public IReadOnlyList<PlcMlProfileStatus> GetStatus() => Array.Empty<PlcMlProfileStatus>();
    }

    private sealed class TrainingStoreStub : IPlcMlTrainingStore
    {
        public Task<int> CountAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task AppendAsync(PlcFeatureVector vector, int maximumWindows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(string profileId, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(Array.Empty<PlcFeatureVector>());
        public Task<PlcMlDatasetInfo> CreateDatasetAsync(string profileId, int maximumWindows, string createdBy, string? description, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlDatasetInfo>>(Array.Empty<PlcMlDatasetInfo>());
        public Task<IReadOnlyList<PlcFeatureVector>> ReadDatasetAsync(string profileId, string datasetVersion, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(Array.Empty<PlcFeatureVector>());
    }

    private sealed class GovernanceStore : IPlcMlGovernanceStore
    {
        public GovernanceStore(PlcMlModelGovernanceInfo model) => Model = model;
        public PlcMlModelGovernanceInfo Model { get; private set; }

        public Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecoverCandidateAsync(string candidateId, DateTime recoveredUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(string? profileId, PlcMlReviewDecision? decision, int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlCandidateRecord>>(Array.Empty<PlcMlCandidateRecord>());
        public Task<PlcMlCandidateRecord> ReviewCandidateAsync(string candidateId, PlcMlReviewDecision decision, string reviewedBy, string? comment, DateTime reviewedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default)
        {
            Model = model;
            return Task.CompletedTask;
        }
        public Task<PlcMlModelGovernanceInfo?> GetModelAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcMlModelGovernanceInfo?>(Model.ProfileId == profileId && Model.ModelVersion == version ? Model : null);
        public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelGovernanceInfo>>(new[] { Model });
        public Task<PlcMlModelGovernanceInfo> DecideModelAsync(string profileId, string version, PlcMlApprovalStatus status, string actor, string? comment, DateTime decidedUtc, CancellationToken cancellationToken = default)
        {
            Model = Model with
            {
                ApprovalStatus = status,
                DecidedBy = actor,
                DecidedUtc = decidedUtc,
                DecisionComment = comment
            };
            return Task.FromResult(Model);
        }
        public Task<bool> IsModelApprovedAsync(string profileId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(Model.ApprovalStatus == PlcMlApprovalStatus.Approved);
        public Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcMlDriftSnapshot?>(null);
        public Task<PlcMlEvaluationSummary> GetEvaluationAsync(string profileId, string? modelVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlcMlEvaluationSummary { ProfileId = profileId, ModelVersion = modelVersion });
    }
}
