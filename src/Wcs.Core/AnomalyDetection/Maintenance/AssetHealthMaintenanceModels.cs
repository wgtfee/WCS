namespace Wcs.Core.AnomalyDetection.Maintenance;

using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.RootCause;

public enum MaintenanceRecommendationStatus
{
    Proposed = 0,
    Accepted = 1,
    Rejected = 2,
    InProgress = 3,
    Completed = 4,
    Cancelled = 5
}

public enum MaintenanceFeedbackDecision
{
    Accepted = 0,
    Rejected = 1,
    FalsePositive = 2,
    Repaired = 3,
    NoFaultFound = 4,
    Cancelled = 5
}

public enum MaintenanceTrainingLabelStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2
}

public sealed class AssetHealthMaintenanceOptions
{
    public bool Enabled { get; set; }
    public int EvaluationIntervalSeconds { get; set; } = 30;
    public int MaximumRules { get; set; } = 10_000;
    public int MaximumItemsPerRecommendation { get; set; } = 100;
    public int MaximumRecommendationsQueryCount { get; set; } = 1_000;
    public double MinimumRootCauseConfidence { get; set; } = 0.25;
    public int RecommendationRetentionHours { get; set; } = 8_760;
    public int MaintenanceIntervalSeconds { get; set; } = 3_600;
    public int MaintenanceBatchSize { get; set; } = 2_000;
    public MaintenanceRuleSetDefinition RuleSet { get; set; } = new();
}

public sealed class MaintenanceRuleSetDefinition
{
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public List<MaintenanceDecisionRule> Rules { get; set; } = new();
}

public sealed class MaintenanceDecisionRule
{
    public string RuleId { get; set; } = string.Empty;
    public string? RootCauseNodeId { get; set; }
    public RootCauseNodeKind? RootCauseKind { get; set; }
    public AssetHealthGrade MinimumEventGrade { get; set; } = AssetHealthGrade.Degraded;
    public string Title { get; set; } = string.Empty;
    public int Priority { get; set; } = 3;
    public int EstimatedMinutes { get; set; } = 30;
    public List<string> InspectionItems { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public List<string> Tools { get; set; } = new();
    public List<string> SpareParts { get; set; } = new();
    public List<string> SafetyNotes { get; set; } = new();
}

public sealed record MaintenanceRuleSetRegistration
{
    public required string Version { get; init; }
    public required string RuleSetHash { get; init; }
    public required string Source { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTime ApprovedAtUtc { get; init; }
    public required DateTime RegisteredAtUtc { get; init; }
    public required int RuleCount { get; init; }
    public required string RuleSetJson { get; init; }
}

public sealed record AssetHealthMaintenanceRecommendation
{
    public required string RecommendationId { get; init; }
    public required string AnalysisId { get; init; }
    public required string TriggerEventId { get; init; }
    public required int TriggerEventVersion { get; init; }
    public required string AssetId { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string RuleSetHash { get; init; }
    public required string RuleId { get; init; }
    public required string RootCauseNodeId { get; init; }
    public required string RootCauseEntityId { get; init; }
    public required string RootCauseDisplayName { get; init; }
    public required RootCauseNodeKind RootCauseKind { get; init; }
    public required double RootCauseConfidence { get; init; }
    public required RootCauseReviewDecision RootCauseReviewDecision { get; init; }
    public required AssetHealthGrade EventGrade { get; init; }
    public required double PreMaintenanceHealthScore { get; init; }
    public double? PostMaintenanceHealthScore { get; init; }
    public required string Title { get; init; }
    public required int Priority { get; init; }
    public required int EstimatedMinutes { get; init; }
    public required IReadOnlyList<string> InspectionItems { get; init; }
    public required IReadOnlyList<string> Components { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required IReadOnlyList<string> SpareParts { get; init; }
    public required IReadOnlyList<string> SafetyNotes { get; init; }
    public required string Explanation { get; init; }
    public required MaintenanceRecommendationStatus Status { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public string? MesWorkOrderNo { get; init; }
    public string? AssignedTo { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public MaintenanceFeedbackDecision? LatestFeedbackDecision { get; init; }
    public string? LatestFeedbackActor { get; init; }
    public DateTime? LatestFeedbackAtUtc { get; init; }
    public string? LatestFeedbackNote { get; init; }
}

public sealed record AssetHealthMaintenanceFeedback
{
    public required string FeedbackId { get; init; }
    public required string RecommendationId { get; init; }
    public required MaintenanceFeedbackDecision Decision { get; init; }
    public required string Actor { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
    public double? PostHealthScore { get; init; }
    public string? MesWorkOrderNo { get; init; }
    public string? AssignedTo { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? Note { get; init; }
}

public sealed record MaintenanceTrainingLabelCandidate
{
    public required string CandidateId { get; init; }
    public required string RecommendationId { get; init; }
    public required string AnalysisId { get; init; }
    public required string EventId { get; init; }
    public required string AssetId { get; init; }
    public required string RootCauseNodeId { get; init; }
    public required string Label { get; init; }
    public required MaintenanceFeedbackDecision SourceDecision { get; init; }
    public required MaintenanceTrainingLabelStatus Status { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
    public string? ReviewNote { get; init; }
}

public sealed record AssetHealthMaintenanceMetrics
{
    public required int TotalRecommendations { get; init; }
    public required int AcceptedRecommendations { get; init; }
    public required int RejectedRecommendations { get; init; }
    public required int CompletedRecommendations { get; init; }
    public required int FalsePositiveCount { get; init; }
    public required int RepairedCount { get; init; }
    public required int NoFaultFoundCount { get; init; }
    public required double AcceptanceRate { get; init; }
    public required double ConfirmedFaultRate { get; init; }
    public required double FalsePositiveRate { get; init; }
    public double? AverageClosureMinutes { get; init; }
}

public sealed record AssetHealthMaintenanceStatus
{
    public required bool Enabled { get; init; }
    public required bool RootCauseEnabled { get; init; }
    public required bool RuleSetValid { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string RuleSetHash { get; init; }
    public required int RuleCount { get; init; }
    public DateTime? LastEvaluationUtc { get; init; }
    public string? LastError { get; init; }
}

public sealed record AssetHealthMaintenanceStoreStatus
{
    public required bool Enabled { get; init; }
    public required string Provider { get; init; }
    public required bool IsAvailable { get; init; }
    public required int RegisteredRuleSets { get; init; }
    public required int RetainedRecommendations { get; init; }
    public required int RetainedFeedbackRows { get; init; }
    public required int PendingTrainingLabels { get; init; }
    public DateTime? LastSuccessfulWriteUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IAssetHealthMaintenanceDecisionEngine
{
    MaintenanceRuleSetRegistration RuleSetRegistration { get; }

    AssetHealthMaintenanceRecommendation? Generate(
        AssetHealthRootCauseAnalysisSnapshot analysis,
        AssetHealthEventSnapshot healthEvent,
        DateTime utcNow);
}

public interface IAssetHealthMaintenanceStore
{
    string Provider { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask RegisterRuleSetAsync(
        MaintenanceRuleSetRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SaveRecommendationAsync(
        AssetHealthMaintenanceRecommendation recommendation,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthMaintenanceRecommendation?> GetRecommendationAsync(
        string recommendationId,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthMaintenanceRecommendation?> GetLatestForAnalysisAsync(
        string analysisId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthMaintenanceRecommendation>> GetRecommendationsAsync(
        MaintenanceRecommendationStatus? status = null,
        string? assetId = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthMaintenanceRecommendation?> AppendFeedbackAsync(
        AssetHealthMaintenanceFeedback feedback,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthMaintenanceFeedback>> GetFeedbackAsync(
        string recommendationId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MaintenanceTrainingLabelCandidate>> GetTrainingLabelCandidatesAsync(
        MaintenanceTrainingLabelStatus? status = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);

    ValueTask<MaintenanceTrainingLabelCandidate?> ReviewTrainingLabelAsync(
        string candidateId,
        MaintenanceTrainingLabelStatus status,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthMaintenanceMetrics> GetMetricsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthMaintenanceStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
