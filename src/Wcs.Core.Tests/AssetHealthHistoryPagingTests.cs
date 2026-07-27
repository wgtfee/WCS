namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetHealthHistoryPagingTests
{
    [Fact]
    public async Task Page_queries_are_newest_first_across_pages_but_items_are_chronological()
    {
        var store = new InMemoryAssetHealthScoreHistoryStore(Options());
        var start = DateTime.UnixEpoch;
        for (var index = 0; index < 5; index++)
            await store.RecordAsync(Snapshot(90 - index), start.AddMinutes(index));

        var first = await store.GetHistoryPageAsync("RGV-PAGE", skip: 0, maximumCount: 2);
        var second = await store.GetHistoryPageAsync("RGV-PAGE", skip: 2, maximumCount: 2);

        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.Equal(new[] { 87d, 86d }, first.Items.Select(static point => point.HealthScore));
        Assert.Equal(new[] { 89d, 88d }, second.Items.Select(static point => point.HealthScore));
    }

    [Fact]
    public async Task Range_trend_uses_only_points_inside_requested_window()
    {
        var store = new InMemoryAssetHealthScoreHistoryStore(Options());
        var start = DateTime.UnixEpoch;
        await store.RecordAsync(Snapshot(95), start);
        await store.RecordAsync(Snapshot(80), start.AddHours(1));
        await store.RecordAsync(Snapshot(70), start.AddHours(2));
        await store.RecordAsync(Snapshot(90), start.AddHours(3));

        var trend = await store.GetTrendRangeAsync(
            "RGV-PAGE",
            start.AddHours(1),
            start.AddHours(2),
            windowSize: 10);

        Assert.NotNull(trend);
        Assert.Equal(2, trend!.SampleCount);
        Assert.Equal(AssetHealthTrendDirection.Deteriorating, trend.Direction);
        Assert.Equal(-10, trend.ScoreDelta);
    }

    private static AssetHealthScoringOptions Options() => new()
    {
        Enabled = true,
        MinimumScoreChangeToRecord = 0,
        MaximumUnchangedIntervalSeconds = 300,
        MaximumHistoryPerAsset = 100,
        MaximumTrackedHistoryAssets = 100,
        HistoryRetentionHours = 24,
        TrendWindowSize = 12,
        TrendChangeThreshold = 2,
        MaximumHistoryQueryCount = 100
    };

    private static AssetHealthScoreSnapshot Snapshot(double score) => new()
    {
        AssetId = "RGV-PAGE",
        HealthScore = score,
        Grade = score >= 85
            ? AssetHealthGrade.Healthy
            : score >= 70
                ? AssetHealthGrade.Attention
                : score >= 40
                    ? AssetHealthGrade.Degraded
                    : AssetHealthGrade.Critical,
        FusionRiskScore = (100 - score) / 100,
        FusionStatus = score >= 85
            ? FusedHealthStatus.Normal
            : score >= 70
                ? FusedHealthStatus.Observe
                : score >= 40
                    ? FusedHealthStatus.Warning
                    : FusedHealthStatus.Alarm,
        IndependentSourceCount = 1,
        CalculatedAtUtc = DateTime.UnixEpoch,
        Factors = Array.Empty<AssetHealthFactor>(),
        Summary = $"score={score:F2}"
    };
}