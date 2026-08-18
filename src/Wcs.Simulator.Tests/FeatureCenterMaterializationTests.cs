namespace Wcs.Simulator.Tests;

using Wcs.FeatureCenter;

public sealed class FeatureCenterMaterializationTests
{
    [Fact]
    public async Task Rebuild_is_deterministic_for_out_of_order_observations()
    {
        var definition = Definition();
        var schema = ApprovedSchema(definition);
        var t1 = DateTimeOffset.Parse("2026-01-01T01:00:00Z");
        var t2 = t1.AddHours(1);
        var observations = new[]
        {
            new FeatureObservation("asset-1", "health.latest", 20d, t2, "health", "2"),
            new FeatureObservation("asset-1", "health.latest", 10d, t1, "health", "1")
        };

        var first = new BoundedFeatureRealtimeCache([definition]);
        await first.RebuildAsync(observations, default);
        var second = new BoundedFeatureRealtimeCache([definition]);
        await second.RebuildAsync(observations.Reverse(), default);

        var firstValue = await first.ReadAsOfAsync("asset-1", schema, t1.AddMinutes(1), default);
        var secondValue = await second.ReadAsOfAsync("asset-1", schema, t1.AddMinutes(1), default);
        Assert.Equal(firstValue.Single().Value, secondValue.Single().Value);
        Assert.Equal(10d, firstValue.Single().Value);
    }

    [Fact]
    public async Task Realtime_cache_enforces_entity_bound()
    {
        var definition = Definition();
        var cache = new BoundedFeatureRealtimeCache([definition], maximumEntities: 1);
        var at = DateTimeOffset.Parse("2026-01-01T01:00:00Z");
        await cache.ApplyAsync(new("asset-1", "health.latest", 10d, at, "health", "1"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.ApplyAsync(new("asset-2", "health.latest", 20d, at, "health", "2"), default));
    }

    [Fact]
    public async Task Point_in_time_dataset_uses_only_values_visible_at_anchor()
    {
        var definition = Definition();
        var schema = ApprovedSchema(definition);
        var cache = new BoundedFeatureRealtimeCache([definition]);
        var anchor = DateTimeOffset.Parse("2026-01-01T02:00:00Z");
        await cache.ApplyAsync(new("asset-1", "health.latest", 10d, anchor.AddMinutes(-10), "health", "1"), default);
        await cache.ApplyAsync(new("asset-1", "health.latest", 99d, anchor.AddMinutes(10), "health", "2"), default);
        var builder = new PointInTimeDatasetBuilder(cache);
        var request = new PointInTimeDatasetRequest(
            "dataset", "1", schema, anchor.AddHours(-1), anchor.AddHours(1),
            [("asset-1", anchor, anchor.AddHours(2))], 100,
            "file:///datasets/dataset-1.parquet", new string('a', 64), "tester", "corr-1");

        var result = await builder.BuildAsync(request, default);

        Assert.Single(result.Rows);
        Assert.Equal(10d, result.Rows[0].Values["health.latest"]);
        Assert.Equal(1, result.Manifest.RowCount);
    }

    [Fact]
    public async Task Point_in_time_dataset_hash_is_replay_deterministic()
    {
        var definition = Definition();
        var schema = ApprovedSchema(definition);
        var cache = new BoundedFeatureRealtimeCache([definition]);
        var anchor = DateTimeOffset.Parse("2026-01-01T02:00:00Z");
        await cache.ApplyAsync(new("asset-1", "health.latest", 42d, anchor, "health", "7"), default);
        var builder = new PointInTimeDatasetBuilder(cache);
        var request = new PointInTimeDatasetRequest(
            "dataset", "1", schema, anchor, anchor,
            [("asset-1", anchor, (DateTimeOffset?)null)], 100,
            "file:///datasets/dataset-1.parquet", new string('b', 64), "tester", "corr-2");

        var first = await builder.BuildAsync(request, default);
        var second = await builder.BuildAsync(request, default);

        Assert.Equal(first.Manifest.DatasetHash, second.Manifest.DatasetHash);
        Assert.Equal(first.Rows.Single().Values["health.latest"], second.Rows.Single().Values["health.latest"]);
    }

    private static FeatureDefinition Definition() => FeatureDefinition.Create(
        "health.latest", "Latest health", "Asset", FeatureDataType.Double, "score", "health",
        "latest", TimeSpan.Zero, TimeSpan.FromHours(1), FeatureNullPolicy.Fail, null, 0, 100, "1", "idi-p2");

    private static FeatureSchema ApprovedSchema(FeatureDefinition definition) =>
        FeatureSchema.Create("v3.9-governed", "1", [new(definition.FeatureId, definition.DefinitionHash, 0)])
            .Approve("tester", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
