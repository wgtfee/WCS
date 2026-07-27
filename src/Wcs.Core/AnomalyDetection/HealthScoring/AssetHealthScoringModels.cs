namespace Wcs.Core.AnomalyDetection.HealthScoring;

using Wcs.Core.AnomalyDetection.Fusion;

public enum AssetHealthGrade
{
    Healthy = 0,
    Attention = 1,
    Degraded = 2,
    Critical = 3
}

public sealed class AssetHealthScoringOptions
{
    public bool Enabled { get; set; }
    public double HealthyMinimumScore { get; set; } = 85;
    public double AttentionMinimumScore { get; set; } = 70;
    public double DegradedMinimumScore { get; set; } = 40;
    public int MaximumFactors { get; set; } = 10;
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

public interface IAssetHealthScoringService
{
    AssetHealthScoreSnapshot? Evaluate(FusedHealthSnapshot snapshot);
    AssetHealthScoreSnapshot? GetAsset(string assetId);
    IReadOnlyList<AssetHealthScoreSnapshot> GetAssets(
        AssetHealthGrade? minimumGrade = null,
        int maximumCount = 200);
    AssetHealthScoringStatus GetStatus();
}
