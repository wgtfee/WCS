namespace Wcs.Core.AnomalyDetection.HealthGovernance;

using Wcs.Core.AnomalyDetection.HealthScoring;

public enum AssetHealthEventLifecycleStatus
{
    Active = 0,
    Recovered = 1
}

public enum AssetHealthEventTransitionType
{
    Raised = 0,
    Observed = 1,
    GradeChanged = 2,
    Acknowledged = 3,
    Suppressed = 4,
    Unsuppressed = 5,
    Recovered = 6
}

public enum AssetHealthDeliveryStatus
{
    Disabled = 0,
    Pending = 1,
    Retrying = 2,
    Delivered = 3,
    Suppressed = 4,
    DeadLetter = 5
}

public sealed class AssetHealthGovernanceOptions
{
    public bool Enabled { get; set; }
    public AssetHealthGrade MinimumEventGrade { get; set; } = AssetHealthGrade.Degraded;
    public int ConsecutiveUnhealthyEvaluations { get; set; } = 3;
    public int ConsecutiveRecoveryEvaluations { get; set; } = 3;
    public int EvaluationIntervalSeconds { get; set; } = 10;
    public int MaximumUnchangedEventIntervalSeconds { get; set; } = 300;
    public int MaximumTrackedAssets { get; set; } = 10_000;
    public int MaximumEventsQueryCount { get; set; } = 1_000;
    public int InactiveStateRetentionSeconds { get; set; } = 86_400;
    public int EventRetentionHours { get; set; } = 2_160;
    public int MaintenanceIntervalSeconds { get; set; } = 3_600;
    public int MaintenanceBatchSize { get; set; } = 2_000;

    public bool MesPushEnabled { get; set; }
    public string MesBaseUrl { get; set; } = string.Empty;
    public string MesEndpointPath { get; set; } = "/api/wcs/asset-health-events";
    public int MesTimeoutSeconds { get; set; } = 5;
    public int MesPollIntervalSeconds { get; set; } = 2;
    public int MesBatchSize { get; set; } = 100;
    public int MesMaximumAttempts { get; set; } = 10;
    public int MesInitialRetrySeconds { get; set; } = 5;
    public int MesMaximumRetrySeconds { get; set; } = 300;
    public string MesApiKeyHeader { get; set; } = string.Empty;
    public string MesApiKey { get; set; } = string.Empty;
}

public sealed record AssetHealthEventSnapshot
{
    public required string EventId { get; init; }
    public required string EventKey { get; init; }
    public required string AssetId { get; init; }
    public required int Version { get; init; }
    public required AssetHealthEventLifecycleStatus LifecycleStatus { get; init; }
    public required AssetHealthGrade Grade { get; init; }
    public required AssetHealthGrade PeakGrade { get; init; }
    public required double HealthScore { get; init; }
    public required double LowestHealthScore { get; init; }
    public required DateTime FirstDetectedUtc { get; init; }
    public required DateTime LastObservedUtc { get; init; }
    public DateTime? RecoveredAtUtc { get; init; }
    public required bool Acknowledged { get; init; }
    public DateTime? AcknowledgedAtUtc { get; init; }
    public string? AcknowledgedBy { get; init; }
    public required bool IsSuppressed { get; init; }
    public DateTime? SuppressedUntilUtc { get; init; }
    public string? SuppressedReason { get; init; }
    public required string Reason { get; init; }
    public required string Source { get; init; }
    public required string Category { get; init; }
}

public sealed record AssetHealthEventTransition
{
    public required long Sequence { get; init; }
    public required string MessageId { get; init; }
    public required AssetHealthEventTransitionType TransitionType { get; init; }
    public required AssetHealthEventSnapshot Event { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
    public string? Actor { get; init; }
    public string? Note { get; init; }
    public required AssetHealthDeliveryStatus DeliveryStatus { get; init; }
    public required int DeliveryAttemptCount { get; init; }
    public DateTime? NextDeliveryAttemptUtc { get; init; }
    public DateTime? LastDeliveryAttemptUtc { get; init; }
    public DateTime? DeliveredAtUtc { get; init; }
    public int? LastHttpStatusCode { get; init; }
    public string? LastDeliveryError { get; init; }
}

public sealed record AssetHealthGovernanceStatus
{
    public required bool Enabled { get; init; }
    public required bool HealthScoringEnabled { get; init; }
    public required bool MesPushEnabled { get; init; }
    public required int TrackedAssets { get; init; }
    public required int RetainedEvents { get; init; }
    public required int ActiveEvents { get; init; }
    public required int AcknowledgedActiveEvents { get; init; }
    public required int SuppressedActiveEvents { get; init; }
    public required AssetHealthGrade MinimumEventGrade { get; init; }
    public required int ConsecutiveUnhealthyEvaluations { get; init; }
    public required int ConsecutiveRecoveryEvaluations { get; init; }
    public required int EvaluationIntervalSeconds { get; init; }
    public DateTime? LastEvaluationUtc { get; init; }
    public string? LastError { get; init; }
}

public sealed record AssetHealthEventJournalStatus
{
    public required bool Enabled { get; init; }
    public required string Provider { get; init; }
    public required bool IsAvailable { get; init; }
    public required int RetainedTransitions { get; init; }
    public required int RetainedEvents { get; init; }
    public required int PendingDeliveries { get; init; }
    public required int RetryingDeliveries { get; init; }
    public required int DeliveredMessages { get; init; }
    public required int DeadLetterMessages { get; init; }
    public DateTime? LastSuccessfulWriteUtc { get; init; }
    public DateTime? LastSuccessfulDeliveryUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IAssetHealthEventJournalStore
{
    string Provider { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> AppendAsync(
        AssetHealthEventTransition transition,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthEventTransition>> LoadLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetHistoryAsync(
        string eventId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetPendingDeliveriesAsync(
        DateTime utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask MarkDeliveredAsync(
        string messageId,
        DateTime deliveredAtUtc,
        int? httpStatusCode,
        CancellationToken cancellationToken = default);

    ValueTask MarkDeliveryFailedAsync(
        string messageId,
        int attemptCount,
        DateTime nextAttemptUtc,
        bool deadLetter,
        int? httpStatusCode,
        string error,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RetryDeliveryAsync(
        string messageId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthEventJournalStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

public interface IAssetHealthGovernanceService
{
    ValueTask<IReadOnlyList<AssetHealthEventTransition>> EvaluateAsync(
        AssetHealthScoreSnapshot snapshot,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthEventSnapshot?> AcknowledgeAsync(
        string eventId,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthEventSnapshot?> SuppressAsync(
        string eventId,
        string actor,
        string reason,
        DateTime? untilUtc,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthEventSnapshot?> UnsuppressAsync(
        string eventId,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    void Restore(IReadOnlyList<AssetHealthEventTransition> latestTransitions);

    AssetHealthEventSnapshot? GetEvent(string eventId);

    IReadOnlyList<AssetHealthEventSnapshot> GetEvents(
        AssetHealthEventLifecycleStatus? lifecycleStatus = null,
        AssetHealthGrade? minimumGrade = null,
        int maximumCount = 200);

    AssetHealthGovernanceStatus GetStatus();

    ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
