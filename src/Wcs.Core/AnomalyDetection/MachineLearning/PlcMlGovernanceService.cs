namespace Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class PlcMlGovernanceService : IPlcMlGovernanceService
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly IReadOnlyDictionary<string, PlcMlProfile> _profiles;
    private readonly IPlcMlTrainingStore _trainingStore;
    private readonly IPlcMlGovernanceStore _governanceStore;
    private readonly IPlcMlAnomalyEngine _engine;

    public PlcMlGovernanceService(
        PlcMlAnomalyOptions options,
        IPlcMlTrainingStore trainingStore,
        IPlcMlGovernanceStore governanceStore,
        IPlcMlAnomalyEngine engine)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _trainingStore = trainingStore ?? throw new ArgumentNullException(nameof(trainingStore));
        _governanceStore = governanceStore ?? throw new ArgumentNullException(nameof(governanceStore));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _profiles = options.Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);
    }

    public Task<PlcMlDatasetInfo> CreateDatasetAsync(
        string profileId,
        string createdBy,
        string? description,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        var profile = GetProfile(profileId);
        return _trainingStore.CreateDatasetAsync(
            profileId,
            profile.MaximumTrainingWindows,
            createdBy,
            description,
            cancellationToken);
    }

    public Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        return _trainingStore.ListDatasetsAsync(profileId, cancellationToken);
    }

    public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(
        string? profileId,
        PlcMlReviewDecision? decision,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        if (!string.IsNullOrWhiteSpace(profileId)) GetProfile(profileId);
        return _governanceStore.QueryCandidatesAsync(
            string.IsNullOrWhiteSpace(profileId) ? null : profileId,
            decision,
            maximumCount,
            cancellationToken);
    }

    public Task<PlcMlCandidateRecord> ReviewCandidateAsync(
        string candidateId,
        PlcMlReviewDecision decision,
        string reviewedBy,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        return _governanceStore.ReviewCandidateAsync(
            candidateId,
            decision,
            reviewedBy,
            comment,
            DateTime.UtcNow,
            cancellationToken);
    }

    public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelGovernanceAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        return _governanceStore.ListModelsAsync(profileId, cancellationToken);
    }

    public async Task<PlcMlModelGovernanceInfo> ApproveModelAsync(
        string profileId,
        string version,
        string approvedBy,
        string? comment,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        var approved = await _governanceStore.DecideModelAsync(
            profileId,
            version,
            PlcMlApprovalStatus.Approved,
            approvedBy,
            comment,
            DateTime.UtcNow,
            cancellationToken);
        if (activate)
            await _engine.ActivateModelAsync(profileId, version, cancellationToken);
        return approved;
    }

    public Task<PlcMlModelGovernanceInfo> RejectModelAsync(
        string profileId,
        string version,
        string rejectedBy,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        return _governanceStore.DecideModelAsync(
            profileId,
            version,
            PlcMlApprovalStatus.Rejected,
            rejectedBy,
            comment,
            DateTime.UtcNow,
            cancellationToken);
    }

    public Task<PlcMlEvaluationSummary> GetEvaluationAsync(
        string profileId,
        string? modelVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        return _governanceStore.GetEvaluationAsync(profileId, modelVersion, cancellationToken);
    }

    public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureManagementEnabled();
        GetProfile(profileId);
        return _governanceStore.GetLatestDriftAsync(profileId, cancellationToken);
    }

    private PlcMlProfile GetProfile(string profileId) =>
        _profiles.TryGetValue(profileId, out var profile)
            ? profile
            : throw new KeyNotFoundException($"未找到 PLC ML Profile：{profileId}。");

    private void EnsureManagementEnabled()
    {
        if (!_options.ManagementApiEnabled)
            throw new InvalidOperationException("PLC ML 管理能力未启用。");
    }
}
