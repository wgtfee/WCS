namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetHealthScoreHistoryStoreTests
{
    [Fact]
    public async Task Small_changes_are_deduplicated_until_heartbeat_is_due()
    {
        var options = Options();
        options.MinimumScoreChangeToRecord = 1;
        options.MaximumUnchangedIntervalSeconds = 60;
        var store = new InMemoryAssetHealthScoreHistoryStore(options);
        var start = DateTime.UnixEpoch;

        Assert.True(await store.RecordAsync(Snapshot("RGV-01", 80, AssetHealthGrade.Attention), start));
        Assert.False(await store.RecordAsync(
            Snapshot("RGV-01", 79.5, AssetHealthGrade.Attention),
            start.AddSeconds(10)));
        Assert.True(await store.RecordAsync(
            Snapshot("RGV-01", 79.5, AssetHealthGrade.Attention),
            start.AddSeconds(61)));

        var history = await store.GetHistoryAsync("RGV-01");
        var status = await store.GetStatusAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(2, status.RecordedPoints);
        Assert.Equal(1, status.DeduplicatedPoints);
    }

    [Fact]
    public async Task Grade_change_is_recorded_even_when_score_change_is_small()
    {
        var options = Options();
        options.MinimumScoreChangeToRecord = 5;
        var store = new InMemoryAssetHealthScoreHistoryStore(options);
        var start = DateTime.UnixEpoch;

        await store.RecordAsync(Snapshot("EMS-01", 70, AssetHealthGrade.Attention), start);
        Assert.True(await store.RecordAsync(
            Snapshot("EMS-01", 69.8, AssetHealthGrade.Degraded),
            start.AddSeconds(10)));

        var history = await store.GetHistoryAsync("EMS-01");
        Assert.Equal(2, history.Count);
        Assert.True(history[1].GradeChanged);
        Assert.Equal(AssetHealthGrade.Attention, history[1].PreviousGrade);
        Assert.Equal(AssetHealthGrade.Degraded, history[1].Grade);
        Assert.Equal(-0.2, history[1].ScoreDelta, 2);
    }

    [Fact]
    public async Task Per_asset_history_and_retention_are_bounded()
    {
        var options = Options();
        options.MinimumScoreChangeToRecord = 0;
        options.MaximumHistoryPerAsset = 3;
        options.HistoryRetentionHours = 1;
        var store = new InMemoryAssetHealthScoreHistoryStore(options);
        var start = DateTime.UnixEpoch;

        for (var index = 0; index < 5; index++)
        {
            await store.RecordAsync(
                Snapshot("CV-01", 100 - index, AssetHealthGrade.Healthy),
                start.AddHours(index));
        }

        var bounded = await store.GetHistoryAsync("CV-01", maximumCount: 10);
        Assert.Equal(3, bounded.Count);
        Assert.Equal(new[] { 98d, 97d, 96d }, bounded.Select(static point => point.HealthScore));

        await store.MaintainAsync(start.AddHours(5));
        var retained = await store.GetHistoryAsync("CV-01", maximumCount: 10);
        var status = await store.GetStatusAsync();
        Assert.Single(retained);
        Assert.Equal(96, retained[0].HealthScore);
        Assert.Equal(4, status.EvictedPoints);
    }

    [Fact]
    public async Task Trend_window_detects_deteriorating_and_improving_scores()
    {
        var options = Options();
        options.MinimumScoreChangeToRecord = 0;
        options.TrendChangeThreshold = 2;
        var store = new InMemoryAssetHealthScoreHistoryStore(options);
        var start = DateTime.UnixEpoch;

        await store.RecordAsync(Snapshot("RGV-DOWN", 90, AssetHealthGrade.Healthy), start);
        await store.RecordAsync(Snapshot("RGV-DOWN", 80, AssetHealthGrade.Attention), start.AddMinutes(30));
        await store.RecordAsync(Snapshot("RGV-DOWN", 70, AssetHealthGrade.Attention), start.AddHours(1));

        await store.RecordAsync(Snapshot("RGV-UP", 40, AssetHealthGrade.Degraded), start);
        await store.RecordAsync(Snapshot("RGV-UP", 60, AssetHealthGrade.Degraded), start.AddHours(1));

        var down = await store.GetTrendAsync("RGV-DOWN");
        var up = await store.GetTrendAsync("RGV-UP");
        Assert.NotNull(down);
        Assert.NotNull(up);
        Assert.Equal(AssetHealthTrendDirection.Deteriorating, down!.Direction);
        Assert.Equal(-20, down.ScoreDelta);
        Assert.Equal(-20, down.HealthScoreSlopePerHour);
        Assert.Equal(AssetHealthTrendDirection.Improving, up!.Direction);
        Assert.Equal(20, up.ScoreDelta);
    }

    [Fact]
    public async Task Asset_capacity_evicts_the_oldest_history()
    {
        var options = Options();
        options.MaximumTrackedHistoryAssets = 2;
        var store = new InMemoryAssetHealthScoreHistoryStore(options);
        var start = DateTime.UnixEpoch;

        await store.RecordAsync(Snapshot("A", 90, AssetHealthGrade.Healthy), start);
        await store.RecordAsync(Snapshot("B", 80, AssetHealthGrade.Attention), start.AddSeconds(1));
        await store.RecordAsync(Snapshot("C", 70, AssetHealthGrade.Attention), start.AddSeconds(2));

        Assert.Empty(await store.GetHistoryAsync("A"));
        Assert.Single(await store.GetHistoryAsync("B"));
        Assert.Single(await store.GetHistoryAsync("C"));
        var status = await store.GetStatusAsync();
        Assert.Equal(2, status.TrackedAssets);
        Assert.Equal(1, status.EvictedAssets);
        Assert.Equal(1, status.EvictedPoints);
    }

    private static AssetHealthScoringOptions Options() => new()
    {
        Enabled = true,
        SamplingIntervalSeconds = 10,
        MinimumScoreChangeToRecord = 1,
        MaximumUnchangedIntervalSeconds = 300,
        MaximumHistoryPerAsset = 100,
        MaximumTrackedHistoryAssets = 100,
        HistoryRetentionHours = 24,
        TrendWindowSize = 12,
        TrendChangeThreshold = 2,
        MaximumHistoryQueryCount = 1_000
    };

    private static AssetHealthScoreSnapshot Snapshot(
        string assetId,
        double healthScore,
        AssetHealthGrade grade) => new()
    {
        AssetId = assetId,
        HealthScore = healthScore,
        Grade = grade,
        FusionRiskScore = Math.Clamp((100 - healthScore) / 100, 0, 1),
        FusionStatus = grade switch
        {
            AssetHealthGrade.Healthy => FusedHealthStatus.Normal,
            AssetHealthGrade.Attention => FusedHealthStatus.Observe,
            AssetHealthGrade.Degraded => FusedHealthStatus.Warning,
            AssetHealthGrade.Critical => FusedHealthStatus.Alarm,
            _ => FusedHealthStatus.Normal
        },
        IndependentSourceCount = grade == AssetHealthGrade.Healthy ? 0 : 1,
        CalculatedAtUtc = DateTime.UnixEpoch,
        Factors = Array.Empty<AssetHealthFactor>(),
        Summary = $"{assetId}:{healthScore:F2}"
    };
}
