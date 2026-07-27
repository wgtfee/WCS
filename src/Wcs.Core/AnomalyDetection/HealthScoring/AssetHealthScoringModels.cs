namespace Wcs.Core.AnomalyDetection.HealthScoring;

using Wcs.Core.AnomalyDetection.Fusion;

public enum AssetHealthGrade
{
    Healthy = 0,
    Attention = 1,
    Degraded = 2,
    Critical = 3
}

public enum AssetHealthTrendDirection
{
    Stable = 0,
    Improving = 1,
    Deteriorating = 2
}

public sealed class AssetHealthScoringOptions
{
    public bool Enabled { get; set; }
    public double HealthyMinimumScore { get; set; } = 85;
    public double AttentionMinimumScore { get; set; } = 70;
    public double DegradedMinimumScore { get; set; } = 40;
    public int MaximumFactors { get; set; } = 10;

    public int SamplingIntervalSeconds { get; set; } = 10;
    public double MinimumScoreChangeToRecord { get; set; } = 1;
    public int MaximumUnchangedIntervalSeconds { get; set; } = 300;
    public int MaximumHistoryPerAsset { get; set; } = 720;
    public int MaximumTrackedHistoryAssets { get; set; } = 20_000;
    public int HistoryRetentionHours { get; set; } = 24;
    public int TrendWindowSize { get; set; } = 12;
    public double TrendChangeThreshold { get; set; } = 2;
    public int MaximumHistoryQueryCount { get; set; } = 1_000;
}

public sealed record AssetHealthFactor
{
    public required string Source { get; init; }
    public required string Category { get; init; }
    public required double Contribution { get; init; }
    public required double Penalty { get; init; }
    public required string Reason { get; init; }
}

public sealed record AssetHealthScoreSnapshot
{
    public required string AssetId { get; init; }
    public required double HealthScore { get; init; }
    public required AssetHealthGrade Grade { get; init; }
    public required double FusionRiskScore { get; init; }
    public required FusedHealthStatus FusionStatus { get; init; }
    public required int IndependentSourceCount { get; init; }
    public required DateTime CalculatedAtUtc { get; init; }
    public required IReadOnlyList<AssetHealthFactor> Factors { get; init; }
    public required string Summary { get; init; }
}

public sealed record AssetHealthScorePoint
{
    public required long Sequence { get; init; }
    public required string AssetId { get; init; }
    public required double HealthScore { get; init; }
    public required double PreviousHealthScore { get; init; }
    public required double ScoreDelta { get; init; }
    public required AssetHealthGrade Grade { get; init; }
    public required AssetHealthGrade PreviousGrade { get; init; }
    public required bool GradeChanged { get; init; }
    public required AssetHealthTrendDirection Direction { get; init; }
    public required double FusionRiskScore { get; init; }
    public required FusedHealthStatus FusionStatus { get; init; }
    public required int IndependentSourceCount { get; init; }
    public required DateTime CalculatedAtUtc { get; init; }
    public required DateTime RecordedAtUtc { get; init; }
    public required string Summary { get; init; }
}

public sealed record AssetHealthTrendSnapshot
{
    public required string AssetId { get; init; }
    public required AssetHealthTrendDirection Direction { get; init; }
    public required double CurrentHealthScore { get; init; }
    public required double ScoreDelta { get; init; }
    public required double AverageHealthScore { get; init; }
    public required double MinimumHealthScore { get; init; }
    public required double MaximumHealthScore { get; init; }
    public required double HealthScoreSlopePerHour { get; init; }
    public required int SampleCount { get; init; }
    public required AssetHealthGrade CurrentGrade { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
}

public sealed record AssetHealthScoringStatus
{
    public required bool Enabled { get; init; }
    public required bool FusionEnabled { get; init; }
    public required int TrackedAssets { get; init; }
    public required double HealthyMinimumScore { get; init; }
    public required double AttentionMinimumScore { get; init; }
    public required double DegradedMinimumScore { get; init; }
    public required int MaximumFactors { get; init; }
}

public sealed record AssetHealthHistoryStoreStatus
{
    public required bool Enabled { get; init; }
    public required string Provider { get; init; }
    public required int TrackedAssets { get; init; }
    public required int RetainedPoints { get; init; }
    public required long RecordedPoints { get; init; }
    public required long DeduplicatedPoints { get; init; }
    public required long EvictedPoints { get; init; }
    public required long EvictedAssets { get; init; }
    public required int MaximumHistoryPerAsset { get; init; }
    public required int MaximumTrackedHistoryAssets { get; init; }
    public required int HistoryRetentionHours { get; init; }
    public required int SamplingIntervalSeconds { get; init; }
}

public interface IAssetHealthScoringService
{
    AssetHealthScoreSnapshot? Evaluate(FusedHealthSnapshot snapshot);
    AssetHealthScoreSnapshot? GetAsset(string assetId);
    IReadOnlyList<AssetHealthScoreSnapshot> GetAssets(
        AssetHealthGrade? minimumGrade = null,
        int maximumCount = 200);
    AssetHealthScoringStatus GetStatus();
}

/// <summary>
/// 健康分历史持久化边界。默认实现为有界内存仓储，后续可替换为 SQL 或时序库实现。
/// </summary>
public interface IAssetHealthScoreHistoryStore
{
    string Provider { get; }

    ValueTask<bool> RecordAsync(
        AssetHealthScoreSnapshot snapshot,
        DateTime recordedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AssetHealthScorePoint>> GetHistoryAsync(
        string assetId,
        DateTime? fromUtc = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthTrendSnapshot?> GetTrendAsync(
        string assetId,
        int? windowSize = null,
        CancellationToken cancellationToken = default);

    ValueTask<AssetHealthHistoryStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
