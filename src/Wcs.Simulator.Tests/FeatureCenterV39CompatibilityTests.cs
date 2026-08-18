namespace Wcs.Simulator.Tests;

using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.FeatureCenter;

public sealed class FeatureCenterV39CompatibilityTests
{
    [Fact]
    public void Governed_feature_names_exactly_match_v39_order()
    {
        Assert.Equal(14, V39ForecastFeatureSchema.FeatureNames.Length);
        Assert.Equal(AssetFailureForecastFeatureSchema.Names, V39ForecastFeatureSchema.FeatureNames);
    }

    [Fact]
    public void Governed_v39_definitions_cover_exactly_fourteen_features()
    {
        var definitions = V39ForecastFeatureSchema.CreateDefinitions();
        Assert.Equal(14, definitions.Count);
        Assert.Equal(V39ForecastFeatureSchema.FeatureNames, definitions.Select(x => x.FeatureId));
        Assert.All(definitions, definition =>
        {
            Assert.Equal(FeatureNullPolicy.Fail, definition.NullPolicy);
            Assert.True(definition.Freshness > TimeSpan.Zero);
            Assert.False(string.IsNullOrWhiteSpace(definition.Unit));
            Assert.False(string.IsNullOrWhiteSpace(definition.DefinitionHash));
        });
    }

    [Fact]
    public void Approved_v39_schema_is_deterministic_and_ordered()
    {
        var at = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);
        var first = V39ForecastFeatureSchema.CreateApprovedSchema("governance", at);
        var second = V39ForecastFeatureSchema.CreateApprovedSchema("another-actor", at.AddHours(1));

        Assert.Equal(FeatureSchemaStatus.Approved, first.Status);
        Assert.Equal(first.SchemaHash, second.SchemaHash);
        Assert.Equal(V39ForecastFeatureSchema.FeatureNames, first.Items.OrderBy(x => x.Ordinal).Select(x => x.FeatureId));
    }

    [Fact]
    public void Model_manifest_match_requires_exact_schema_identity_and_hash()
    {
        var schema = V39ForecastFeatureSchema.CreateApprovedSchema("governance", DateTimeOffset.UtcNow);

        Assert.True(V39ForecastFeatureSchema.MatchesModelManifest(schema, schema.SchemaId, schema.SchemaHash));
        Assert.False(V39ForecastFeatureSchema.MatchesModelManifest(schema, schema.SchemaId + "-other", schema.SchemaHash));
        Assert.False(V39ForecastFeatureSchema.MatchesModelManifest(schema, schema.SchemaId, new string('0', 64)));
    }

    [Fact]
    public void Draft_schema_can_never_match_model_manifest()
    {
        var approved = V39ForecastFeatureSchema.CreateApprovedSchema("governance", DateTimeOffset.UtcNow);
        var draft = FeatureSchema.Create(approved.SchemaId, approved.Version, approved.Items);

        Assert.False(V39ForecastFeatureSchema.MatchesModelManifest(draft, approved.SchemaId, approved.SchemaHash));
    }
}
