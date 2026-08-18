namespace Wcs.MaintenanceLearning;

public sealed record MaintenanceIntervention(
    string InterventionId,
    string AssetId,
    string AssetType,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string PreFeatureSnapshotId,
    string? PostFeatureSnapshotId,
    string ActionType,
    decimal Cost,
    string Actor,
    string CorrelationId);

public sealed record MaintenanceOutcome(
    string OutcomeId,
    string InterventionId,
    DateTimeOffset ObservedAt,
    bool FailureObserved,
    decimal DowntimeMinutes,
    decimal ActualCost,
    string? FailureCode,
    string SourceEventId);

public sealed record EvaluationWindow(string Version, TimeSpan Before, TimeSpan After);

public sealed record MaintenanceEffectiveness(
    string InterventionId,
    string EvaluationWindowVersion,
    decimal DowntimeDeltaMinutes,
    decimal CostDelta,
    bool FailureAvoided,
    string EvidenceHash);

public sealed record CausalCandidate(
    string CandidateId,
    string InterventionId,
    string Treatment,
    string OutcomeMetric,
    string EvidenceHash);

public sealed record CounterfactualEstimate(
    string CandidateId,
    decimal ObservedValue,
    decimal CounterfactualValue,
    decimal EstimatedEffect,
    string MethodVersion,
    string EvidenceHash);

public enum TrainingLabelApprovalState
{
    Pending,
    Approved,
    Rejected
}

public sealed record TrainingLabelCandidate(
    string LabelId,
    string InterventionId,
    string DatasetKey,
    string Label,
    TrainingLabelApprovalState State,
    string EvidenceHash,
    DateTimeOffset CreatedAt);

public sealed record TrainingLabelApproval(
    string LabelId,
    TrainingLabelApprovalState State,
    string Actor,
    string Reason,
    DateTimeOffset DecidedAt);

public static class TrainingDatasetAdmission
{
    public static bool CanEnterDataset(TrainingLabelCandidate candidate) =>
        candidate.State == TrainingLabelApprovalState.Approved;
}
