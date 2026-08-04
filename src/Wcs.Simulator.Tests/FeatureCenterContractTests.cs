namespace Wcs.Simulator.Tests;

using Wcs.FeatureCenter;

public sealed class FeatureCenterContractTests
{
    [Fact]
    public void Definition_hash_is_deterministic()
    {
        var a = D("health.latest");
        var b = D("health.latest");
        Assert.Equal(a.DefinitionHash, b.DefinitionHash);
        Assert.Equal(64, a.DefinitionHash.Length);
    }

    [Fact]
    public void Unit_change_changes_definition_hash()
    {
        var a = D("x", unit: "score");
        var b = D("x", unit: "ratio");
        Assert.NotEqual(a.DefinitionHash, b.DefinitionHash);
    }

    [Fact]
    public void Window_change_changes_definition_hash()
    {
        var a = D("x", window: TimeSpan.FromHours(1));
        var b = D("x", window: TimeSpan.FromHours(2));
        Assert.NotEqual(a.DefinitionHash, b.DefinitionHash);
    }

    [Fact]
    public void Schema_order_change_changes_hash()
    {
        var a = D("a"); var b = D("b");
        var s1 = FeatureSchema.Create("schema", "1", [new(a.FeatureId, a.DefinitionHash, 0), new(b.FeatureId, b.DefinitionHash, 1)]);
        var s2 = FeatureSchema.Create("schema", "1", [new(b.FeatureId, b.DefinitionHash, 0), new(a.FeatureId, a.DefinitionHash, 1)]);
        Assert.NotEqual(s1.SchemaHash, s2.SchemaHash);
    }

    [Fact]
    public void Schema_rejects_duplicate_feature_ids()
    {
        var d = D("a");
        Assert.Throws<ArgumentException>(() => FeatureSchema.Create("s", "1", [new("a", d.DefinitionHash, 0), new("a", d.DefinitionHash, 1)]));
    }

    [Fact]
    public void Schema_rejects_non_contiguous_ordinals()
    {
        var d = D("a");
        Assert.Throws<ArgumentException>(() => FeatureSchema.Create("s", "1", [new("a", d.DefinitionHash, 1)]));
    }

    [Fact]
    public void Stale_value_is_marked_stale()
    {
        var d = D("x", freshness: TimeSpan.FromMinutes(1));
        var result = new FeatureQualityValidator().Validate(d, 50d, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
        Assert.Equal(FeatureQualityStatus.Stale, result.QualityStatus);
    }

    [Fact]
    public void Null_fail_is_missing()
    {
        var d = D("x", nullPolicy: FeatureNullPolicy.Fail);
        var result = new FeatureQualityValidator().Validate(d, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(FeatureQualityStatus.Missing, result.QualityStatus);
    }

    [Fact]
    public void Null_default_is_valid_and_uses_default()
    {
        var d = FeatureDefinition.Create("x", "x", "Asset", FeatureDataType.Double, "score", "src", "latest", TimeSpan.Zero,
            TimeSpan.FromMinutes(5), FeatureNullPolicy.Default, "12.5", 0, 100, "1", "owner");
        var result = new FeatureQualityValidator().Validate(d, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(FeatureQualityStatus.Valid, result.QualityStatus);
        Assert.Equal("12.5", result.Value);
    }

    [Fact]
    public void Null_ignore_is_valid()
    {
        var d = D("x", nullPolicy: FeatureNullPolicy.Ignore);
        var result = new FeatureQualityValidator().Validate(d, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(FeatureQualityStatus.Valid, result.QualityStatus);
    }

    [Fact]
    public void Out_of_range_value_is_detected()
    {
        var result = new FeatureQualityValidator().Validate(D("x"), 101d, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(FeatureQualityStatus.OutOfRange, result.QualityStatus);
    }

    [Fact]
    public void Catalog_contains_documented_initial_features()
    {
        var ids = FeatureCatalogV1.CreateDefault().Select(x => x.FeatureId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(16, ids.Count);
        Assert.Contains("health.latest", ids);
        Assert.Contains("fusionRisk.maximum", ids);
        Assert.Contains("alarm.activeCount", ids);
        Assert.Contains("vehicle.busyRatio", ids);
        Assert.Contains("maintenance.hoursSinceLast", ids);
    }

    [Fact]
    public async Task Registry_rejects_same_version_different_hash()
    {
        var registry = new InMemoryFeatureDefinitionRegistry();
        await registry.RegisterAsync(D("x", unit: "score"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(D("x", unit: "ratio"), default));
    }

    [Fact]
    public async Task Schema_registry_requires_registered_definition_hash()
    {
        var definitions = new InMemoryFeatureDefinitionRegistry();
        var schemas = new InMemoryFeatureSchemaRegistry(definitions);
        var d = D("x");
        var schema = FeatureSchema.Create("s", "1", [new("x", d.DefinitionHash, 0)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => schemas.RegisterAsync(schema, default));
    }

    [Fact]
    public async Task Snapshot_hash_is_deterministic()
    {
        var d = D("x");
        var schema = FeatureSchema.Create("s", "1", [new("x", d.DefinitionHash, 0)]).Approve("tester", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var at = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        var values = new[] { new FeatureValue("x", 12.5d, FeatureQualityStatus.Valid, at) };
        var service = new FeatureSnapshotService();
        var a = await service.FreezeAsync("asset-1", at, schema, values, [], "m1", default);
        var b = await service.FreezeAsync("asset-1", at, schema, values, [], "m1", default);
        Assert.Equal(a.ValuesHash, b.ValuesHash);
        Assert.Equal(a.SnapshotId, b.SnapshotId);
    }

    [Fact]
    public async Task Snapshot_rejects_future_feature_value()
    {
        var d = D("x");
        var schema = FeatureSchema.Create("s", "1", [new("x", d.DefinitionHash, 0)]).Approve("tester", DateTimeOffset.UtcNow);
        var at = DateTimeOffset.UtcNow;
        var service = new FeatureSnapshotService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FreezeAsync("asset", at, schema,
            [new("x", 1d, FeatureQualityStatus.Valid, at.AddSeconds(1))], [], "m1", default));
    }

    [Fact]
    public async Task Formal_snapshot_requires_approved_schema()
    {
        var d = D("x");
        var schema = FeatureSchema.Create("s", "1", [new("x", d.DefinitionHash, 0)]);
        var at = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FeatureSnapshotService().FreezeAsync("asset", at, schema,
            [new("x", 1d, FeatureQualityStatus.Valid, at)], [], "m1", default));
    }

    [Fact]
    public void Pit_rule_rejects_future_source_value()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var row = new FeatureDatasetRow("asset", at, new Dictionary<string, object?>());
        Assert.Throws<InvalidOperationException>(() => PointInTimeRules.ValidateRow(row,
            [new("x", 1d, FeatureQualityStatus.Valid, at.AddTicks(1))]));
    }

    [Fact]
    public void Pit_rule_rejects_outcome_at_or_before_asof()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var row = new FeatureDatasetRow("asset", at, new Dictionary<string, object?>(), at);
        Assert.Throws<InvalidOperationException>(() => PointInTimeRules.ValidateRow(row, []));
    }

    [Fact]
    public void Bounded_limits_reject_excessive_dataset_rows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureCenterLimits { MaximumDatasetRows = 50_000_001 }.Validate());
    }

    private static FeatureDefinition D(
        string id,
        string unit = "score",
        TimeSpan? window = null,
        TimeSpan? freshness = null,
        FeatureNullPolicy nullPolicy = FeatureNullPolicy.Fail) =>
        FeatureDefinition.Create(id, id, "Asset", FeatureDataType.Double, unit, "src", "latest", window ?? TimeSpan.Zero,
            freshness ?? TimeSpan.FromMinutes(5), nullPolicy, null, 0, 100, "1", "owner");
}
