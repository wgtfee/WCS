namespace Wcs.Core.AnomalyDetection.MachineLearning;

using SqlSugar;

/// <summary>机器学习模型在生产中的发布模式。</summary>
public enum PlcMlDeploymentMode
{
    Disabled = 0,
    Shadow = 1,
    Canary = 2,
    Active = 3
}

public enum PlcMlReviewDecision
{
    Unreviewed = 0,
    TruePositive = 1,
    FalsePositive = 2,
    ExpectedBehavior = 3,
    NeedsInvestigation = 4
}

public enum PlcMlApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public enum PlcMlDriftStatus
{
    Unknown = 0,
    Stable = 1,
    Warning = 2,
    Critical = 3
}

public sealed record PlcMlCandidateRecord
{
    public required string CandidateId { get; init; }
    public required string CandidateKey { get; init; }
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public required PlcMlDeploymentMode DeploymentMode { get; init; }
    public bool RoutedToActiveLifecycle { get; init; }
    public required string PlcName { get; init; }
    public required string DeviceId { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
    public required double Score { get; init; }
    public required double Threshold { get; init; }
    public required string Explanation { get; init; }
    public string ContextJson { get; init; } = "{}";
    public bool IsActive { get; init; } = true;
    public DateTime DetectedUtc { get; init; }
    public DateTime? RecoveredUtc { get; init; }
    public PlcMlReviewDecision ReviewDecision { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedUtc { get; init; }
    public string? ReviewComment { get; init; }
}

public sealed record PlcMlModelGovernanceInfo
{
    public required string GovernanceId { get; init; }
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public string? DatasetVersion { get; init; }
    public required PlcMlApprovalStatus ApprovalStatus { get; init; }
    public required DateTime RequestedUtc { get; init; }
    public string RequestedBy { get; init; } = "system";
    public DateTime? DecidedUtc { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionComment { get; init; }
    public int TrainingSampleCount { get; init; }
    public int CalibrationSampleCount { get; init; }
    public double DecisionThreshold { get; init; }
}

public sealed record PlcMlDriftSnapshot
{
    public required string SnapshotId { get; init; }
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public required DateTime CalculatedUtc { get; init; }
    public required int SampleCount { get; init; }
    public required double MeanScore { get; init; }
    public required double P95Score { get; init; }
    public required double BaselineMeanScore { get; init; }
    public required double BaselineP95Score { get; init; }
    public required double DriftRatio { get; init; }
    public required PlcMlDriftStatus Status { get; init; }
}

public sealed record PlcMlEvaluationSummary
{
    public required string ProfileId { get; init; }
    public string? ModelVersion { get; init; }
    public int TotalCandidates { get; init; }
    public int ReviewedCandidates { get; init; }
    public int TruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int ExpectedBehaviors { get; init; }
    public int NeedsInvestigation { get; init; }
    public int Unreviewed { get; init; }
    public double? Precision { get; init; }
}

public sealed record PlcMlDatasetInfo
{
    public required string ProfileId { get; init; }
    public required string Version { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required int WindowCount { get; init; }
    public required string FeatureHash { get; init; }
    public string CreatedBy { get; init; } = "system";
    public string? Description { get; init; }
    public bool IsFrozen { get; init; } = true;
}

[SugarTable("Wcs_PlcMlCandidate")]
public sealed class PlcMlCandidateEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 40)]
    public string CandidateId { get; set; } = string.Empty;
    [SugarColumn(Length = 220)]
    public string CandidateKey { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string ProfileId { get; set; } = string.Empty;
    [SugarColumn(Length = 40)]
    public string ModelVersion { get; set; } = string.Empty;
    public int DeploymentMode { get; set; }
    public bool RoutedToActiveLifecycle { get; set; }
    [SugarColumn(Length = 100)]
    public string PlcName { get; set; } = string.Empty;
    [SugarColumn(Length = 200)]
    public string DeviceId { get; set; } = string.Empty;
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public double Score { get; set; }
    public double Threshold { get; set; }
    [SugarColumn(Length = 2000)]
    public string Explanation { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ContextJson { get; set; }
    public bool IsActive { get; set; }
    public DateTime DetectedUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? RecoveredUtc { get; set; }
    public int ReviewDecision { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ReviewedBy { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ReviewedUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 2000)]
    public string? ReviewComment { get; set; }
}

[SugarTable("Wcs_PlcMlModelGovernance")]
public sealed class PlcMlModelGovernanceEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 180)]
    public string GovernanceId { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string ProfileId { get; set; } = string.Empty;
    [SugarColumn(Length = 40)]
    public string ModelVersion { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 80)]
    public string? DatasetVersion { get; set; }
    public int ApprovalStatus { get; set; }
    public DateTime RequestedUtc { get; set; }
    [SugarColumn(Length = 100)]
    public string RequestedBy { get; set; } = "system";
    [SugarColumn(IsNullable = true)]
    public DateTime? DecidedUtc { get; set; }
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? DecidedBy { get; set; }
    [SugarColumn(IsNullable = true, Length = 2000)]
    public string? DecisionComment { get; set; }
    public int TrainingSampleCount { get; set; }
    public int CalibrationSampleCount { get; set; }
    public double DecisionThreshold { get; set; }
}

[SugarTable("Wcs_PlcMlDriftSnapshot")]
public sealed class PlcMlDriftSnapshotEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 180)]
    public string SnapshotId { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string ProfileId { get; set; } = string.Empty;
    [SugarColumn(Length = 40)]
    public string ModelVersion { get; set; } = string.Empty;
    public DateTime CalculatedUtc { get; set; }
    public int SampleCount { get; set; }
    public double MeanScore { get; set; }
    public double P95Score { get; set; }
    public double BaselineMeanScore { get; set; }
    public double BaselineP95Score { get; set; }
    public double DriftRatio { get; set; }
    public int Status { get; set; }
}

public interface IPlcMlGovernanceStore
{
    Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default);
    Task RecoverCandidateAsync(string candidateId, DateTime recoveredUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(
        string? profileId,
        PlcMlReviewDecision? decision,
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task<PlcMlCandidateRecord> ReviewCandidateAsync(
        string candidateId,
        PlcMlReviewDecision decision,
        string reviewedBy,
        string? comment,
        DateTime reviewedUtc,
        CancellationToken cancellationToken = default);
    Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default);
    Task<PlcMlModelGovernanceInfo?> GetModelAsync(string profileId, string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PlcMlModelGovernanceInfo> DecideModelAsync(
        string profileId,
        string version,
        PlcMlApprovalStatus status,
        string actor,
        string? comment,
        DateTime decidedUtc,
        CancellationToken cancellationToken = default);
    Task<bool> IsModelApprovedAsync(string profileId, string version, CancellationToken cancellationToken = default);
    Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PlcMlEvaluationSummary> GetEvaluationAsync(
        string profileId,
        string? modelVersion,
        CancellationToken cancellationToken = default);
}

public interface IPlcMlGovernanceService
{
    Task<PlcMlDatasetInfo> CreateDatasetAsync(
        string profileId,
        string createdBy,
        string? description,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(
        string? profileId,
        PlcMlReviewDecision? decision,
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task<PlcMlCandidateRecord> ReviewCandidateAsync(
        string candidateId,
        PlcMlReviewDecision decision,
        string reviewedBy,
        string? comment,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelGovernanceAsync(
        string profileId,
        CancellationToken cancellationToken = default);
    Task<PlcMlModelGovernanceInfo> ApproveModelAsync(
        string profileId,
        string version,
        string approvedBy,
        string? comment,
        bool activate,
        CancellationToken cancellationToken = default);
    Task<PlcMlModelGovernanceInfo> RejectModelAsync(
        string profileId,
        string version,
        string rejectedBy,
        string? comment,
        CancellationToken cancellationToken = default);
    Task<PlcMlEvaluationSummary> GetEvaluationAsync(
        string profileId,
        string? modelVersion,
        CancellationToken cancellationToken = default);
    Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default);
}
