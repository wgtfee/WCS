namespace Wcs.Simulator.VirtualHealth;

using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.Fusion;

public sealed class VirtualHealthOptions
{
    public const string SectionName = "SimulationVirtualHealth";

    public int MaximumAssets { get; set; } = 256;
    public int MaximumSamplesPerAsset { get; set; } = 2_048;
    public int MaximumForecastsPerAsset { get; set; } = 512;
    public int MaximumOutcomesPerAsset { get; set; } = 128;
    public int MaximumGeneratedSamplesPerAction { get; set; } = 1_024;
    public int MaximumAuditRecords { get; set; } = 10_000;
    public int ForecastMinimumHistoryPoints { get; set; } = 48;
    public int ForecastMinimumHistorySpanHours { get; set; } = 24;
    public int ForecastMaximumHistoryPoints { get; set; } = 2_000;
    public int TrendWindowSize { get; set; } = 12;
    public double TrendChangeThreshold { get; set; } = 2;
    public double HealthyMinimumScore { get; set; } = 85;
    public double AttentionMinimumScore { get; set; } = 70;
    public double DegradedMinimumScore { get; set; } = 40;
    public double MaximumRulHours { get; set; } = 17_520;

    public void Validate()
    {
        if (MaximumAssets is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumAssets must be between 1 and 100,000.");
        if (MaximumSamplesPerAsset is < 2 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumSamplesPerAsset must be between 2 and 100,000.");
        if (MaximumForecastsPerAsset is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumForecastsPerAsset must be between 1 and 100,000.");
        if (MaximumOutcomesPerAsset is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumOutcomesPerAsset must be between 1 and 100,000.");
        if (MaximumGeneratedSamplesPerAction is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumGeneratedSamplesPerAction must be between 1 and 100,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (ForecastMinimumHistoryPoints is < 2 || ForecastMinimumHistoryPoints > MaximumSamplesPerAsset)
            throw new InvalidOperationException("SimulationVirtualHealth.ForecastMinimumHistoryPoints is outside the sample capacity.");
        if (ForecastMinimumHistorySpanHours is < 1 or > 8_760)
            throw new InvalidOperationException("SimulationVirtualHealth.ForecastMinimumHistorySpanHours must be between 1 and 8,760.");
        if (ForecastMaximumHistoryPoints < ForecastMinimumHistoryPoints || ForecastMaximumHistoryPoints > MaximumSamplesPerAsset)
            throw new InvalidOperationException("SimulationVirtualHealth.ForecastMaximumHistoryPoints is outside the governed sample range.");
        if (TrendWindowSize is < 2 || TrendWindowSize > MaximumSamplesPerAsset)
            throw new InvalidOperationException("SimulationVirtualHealth.TrendWindowSize is outside the sample capacity.");
        if (!double.IsFinite(TrendChangeThreshold) || TrendChangeThreshold < 0 || TrendChangeThreshold > 100)
            throw new InvalidOperationException("SimulationVirtualHealth.TrendChangeThreshold must be between 0 and 100.");
        if (!double.IsFinite(HealthyMinimumScore) || !double.IsFinite(AttentionMinimumScore) || !double.IsFinite(DegradedMinimumScore) ||
            HealthyMinimumScore is <= 0 or > 100 || AttentionMinimumScore <= 0 || DegradedMinimumScore < 0 ||
            !(HealthyMinimumScore > AttentionMinimumScore && AttentionMinimumScore > DegradedMinimumScore))
            throw new InvalidOperationException("SimulationVirtualHealth health-grade thresholds are invalid.");
        if (!double.IsFinite(MaximumRulHours) || MaximumRulHours <= 0 || MaximumRulHours > 175_200)
            throw new InvalidOperationException("SimulationVirtualHealth.MaximumRulHours must be in (0, 175200].");
    }
}

public enum VirtualHealthOutcomeKind
{
    ObservedFailure = 0,
    PreventiveMaintenance = 1,
    CensoredNoFailure = 2
}

public sealed record VirtualHealthAssetDefinition(
    string AssetId,
    double InitialHealthScore,
    double InitialFusionRiskScore,
    int IndependentSourceCount = 1);

public sealed record VirtualHealthAssetSnapshot(
    string AssetId,
    double HealthScore,
    AssetHealthGrade Grade,
    double FusionRiskScore,
    FusedHealthStatus FusionStatus,
    int IndependentSourceCount,
    long LastSampleOffsetMilliseconds,
    int SampleCount,
    int ForecastCount,
    int OutcomeCount,
    long Version);

public sealed record VirtualHealthSampleSnapshot(
    long Sequence,
    string AssetId,
    long VirtualOffsetMilliseconds,
    DateTimeOffset RecordedAtUtc,
    double HealthScore,
    double PreviousHealthScore,
    double ScoreDelta,
    AssetHealthGrade Grade,
    AssetHealthGrade PreviousGrade,
    bool GradeChanged,
    AssetHealthTrendDirection Direction,
    double FusionRiskScore,
    FusedHealthStatus FusionStatus,
    int IndependentSourceCount,
    string Reason);

public sealed record VirtualHealthFeatureSnapshot(
    string AssetId,
    bool Valid,
    string? Reason,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc,
    int SampleCount,
    double HistorySpanHours,
    IReadOnlyList<string> FeatureNames,
    IReadOnlyList<double> Values);

public sealed record VirtualHealthForecastOracleDefinition(
    double FailureProbability24Hours,
    double FailureProbability72Hours,
    double FailureProbability168Hours,
    double RulLowerHours,
    double RulMedianHours,
    double RulUpperHours,
    string Phase = "degradation");

public sealed record VirtualHealthForecastOracleSnapshot(
    long Sequence,
    string OracleId,
    string AssetId,
    long VirtualOffsetMilliseconds,
    DateTimeOffset ForecastedAtUtc,
    double FailureProbability24Hours,
    double FailureProbability72Hours,
    double FailureProbability168Hours,
    double RulLowerHours,
    double RulMedianHours,
    double RulUpperHours,
    string Phase);

public sealed record VirtualHealthOutcomeSnapshot(
    long Sequence,
    string OutcomeId,
    string AssetId,
    VirtualHealthOutcomeKind Kind,
    long VirtualOffsetMilliseconds,
    DateTimeOffset ObservedAtUtc,
    string Note);

public sealed record VirtualHealthTrendSnapshot(
    string AssetId,
    AssetHealthTrendDirection Direction,
    double CurrentHealthScore,
    double ScoreDelta,
    double AverageHealthScore,
    double MinimumHealthScore,
    double MaximumHealthScore,
    double HealthScoreSlopePerHour,
    int SampleCount,
    AssetHealthGrade CurrentGrade,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc);

public sealed record VirtualHealthAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string Target,
    string? Detail,
    bool Success);

public sealed record VirtualHealthStatus(
    int AssetCount,
    int SampleCount,
    int ForecastCount,
    int OutcomeCount,
    int AuditCount,
    long OperationSequence);
