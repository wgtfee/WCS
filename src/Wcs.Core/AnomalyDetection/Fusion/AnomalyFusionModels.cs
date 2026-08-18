namespace Wcs.Core.AnomalyDetection.Fusion;

using Wcs.Core.AnomalyDetection;

public enum AnomalyEvidenceState
{
    Active = 0,
    Recovered = 1
}

public enum FusedHealthStatus
{
    Normal = 0,
    Observe = 1,
    Warning = 2,
    Alarm = 3
}

public static class AnomalyEvidenceSources
{
    public const string ThresholdRule = "RULE_THRESHOLD";
    public const string RateRule = "RULE_RATE";
    public const string DurationRule = "RULE_DURATION";
    public const string StatisticalRule = "RULE_STATISTICAL";
    public const string ConsistencyRule = "RULE_CONSISTENCY";
    public const string IsolationForest = "ISOLATION_FOREST";
    public const string PeerMedianMad = "PEER_MEDIAN_MAD";
    public const string CycleSequence = "CYCLE_SEQUENCE";
    public const string CyclePhaseDuration = "CYCLE_PHASE_DURATION";
    public const string CycleTotalDuration = "CYCLE_TOTAL_DURATION";
}

public sealed class AnomalyFusionSourcePolicy
{
    public string Source { get; set; } = string.Empty;
    public double Weight { get; set; } = 1.0;
    public double DefaultConfidence { get; set; } = 0.8;
}

public sealed class AnomalyFusionOptions
{
    public bool Enabled { get; set; }
    public int ChannelCapacity { get; set; } = 20_000;
    public int EvidenceRetentionSeconds { get; set; } = 300;
    public int RecoveredEvidenceRetentionSeconds { get; set; } = 60;
    public int InactiveStateRetentionSeconds { get; set; } = 600;
    public int MaximumTrackedAssets { get; set; } = 20_000;
    public int MaximumEvidencePerAsset { get; set; } = 100;
    public int MaximumSnapshots { get; set; } = 5_000;
    public double ObserveThreshold { get; set; } = 0.35;
    public double WarningThreshold { get; set; } = 0.65;
    public double AlarmThreshold { get; set; } = 0.85;
    public double RecoveryThreshold { get; set; } = 0.25;
    public int MinimumIndependentSourcesForAlarm { get; set; } = 2;
    public int ConsecutiveWarningEvaluations { get; set; } = 2;
    public int ConsecutiveAlarmEvaluations { get; set; } = 2;
    public int ConsecutiveRecoveryEvaluations { get; set; } = 3;
    public double SourceDiversityBonus { get; set; } = 0.05;
    public double MaximumSourceDiversityBonus { get; set; } = 0.15;
    public List<AnomalyFusionSourcePolicy> Sources { get; set; } = new();
}

public sealed record AnomalyEvidence
{
    public required string EvidenceId { get; init; }
    public required string Source { get; init; }
    public required string AssetId { get; init; }
    public string? RelatedEntityId { get; init; }
    public required string Category { get; init; }
    public required AnomalyEvidenceState State { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public required double Score { get; init; }
    public required double Confidence { get; init; }
    public PlcAnomalySeverity Severity { get; init; } = PlcAnomalySeverity.Warning;
    public required string Reason { get; init; }
    public string? ContextJson { get; init; }
}

public sealed record FusedEvidenceSummary
{
    public required string EvidenceId { get; init; }
    public required string Source { get; init; }
    public required string Category { get; init; }
    public required double Score { get; init; }
    public required double Confidence { get; init; }
    public required double Contribution { get; init; }
    public PlcAnomalySeverity Severity { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
    public string? RelatedEntityId { get; init; }
    public required string Reason { get; init; }
}

public sealed record FusedHealthSnapshot
{
    public required string AssetId { get; init; }
    public required FusedHealthStatus Status { get; init; }
    public required double Score { get; init; }
    public required int IndependentSourceCount { get; init; }
    public required DateTime FirstObservedAtUtc { get; init; }
    public required DateTime LastEvaluatedAtUtc { get; init; }
    public required IReadOnlyList<FusedEvidenceSummary> Evidence { get; init; }
    public string? Summary { get; init; }
}

public sealed record AnomalyFusionStatus
{
    public bool Enabled { get; init; }
    public int TrackedAssets { get; init; }
    public int ActiveEvidence { get; init; }
    public int RetainedSnapshots { get; init; }
    public long EvidenceAccepted { get; init; }
    public long EvidenceRecovered { get; init; }
    public long EvidenceExpired { get; init; }
    public long EvidenceDropped { get; init; }
    public long EvictedAssets { get; init; }
    public long Evaluations { get; init; }
    public long WarningTransitions { get; init; }
    public long AlarmTransitions { get; init; }
    public long RecoveryTransitions { get; init; }
}

public sealed record AnomalyEvidenceIngressStatus
{
    public bool Enabled { get; init; }
    public int Capacity { get; init; }
    public long Written { get; init; }
    public long Dropped { get; init; }
    public long Read { get; init; }
    public long Pending => Math.Max(0, Written - Dropped - Read);
}

public interface IAnomalyEvidenceSink
{
    bool TryWrite(AnomalyEvidence evidence);
}

public interface IAnomalyEvidenceIngressStatus
{
    AnomalyEvidenceIngressStatus GetStatus();
    void RecordRead();
}

public interface IAnomalyFusionEngine
{
    void Process(AnomalyEvidence evidence);
    void Maintenance(DateTime utcNow);
    FusedHealthSnapshot? GetAsset(string assetId);
    IReadOnlyList<FusedHealthSnapshot> GetAssets(
        FusedHealthStatus? minimumStatus = null,
        int maximumCount = 200);
    AnomalyFusionStatus GetStatus();
}
