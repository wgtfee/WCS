namespace Wcs.FeatureCenter;

/// <summary>
/// Governed P2 representation of the fixed AnomalyEngine v3.9 failure-forecast 14-feature contract.
/// This type intentionally duplicates only the immutable names/order and does not reference Wcs.Core,
/// keeping Wcs.FeatureCenter isolated from control/diagnostic runtime implementation details.
/// </summary>
public static class V39ForecastFeatureSchema
{
    public const string SchemaId = "asset-failure-forecast-v3.9";
    public const string Version = "1.0.0";

    public static readonly string[] FeatureNames =
    {
        "health.latest",
        "health.mean",
        "health.minimum",
        "health.maximum",
        "health.stddev",
        "health.slopePerHour",
        "health.delta",
        "fusionRisk.mean",
        "fusionRisk.maximum",
        "grade.changeCount",
        "grade.degradedOrWorseRatio",
        "grade.criticalRatio",
        "history.sampleCount",
        "history.spanHours"
    };

    public static IReadOnlyList<FeatureDefinition> CreateDefinitions()
    {
        return new[]
        {
            D("health.latest", "Health latest", "score", "health", "latest", TimeSpan.Zero, TimeSpan.FromMinutes(5), 0, 100),
            D("health.mean", "Health mean", "score", "health", "mean", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100),
            D("health.minimum", "Health minimum", "score", "health", "minimum", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100),
            D("health.maximum", "Health maximum", "score", "health", "maximum", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100),
            D("health.stddev", "Health standard deviation", "score", "health", "stddev", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100),
            D("health.slopePerHour", "Health slope per hour", "score/hour", "health", "slope", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), -100, 100),
            D("health.delta", "Health window delta", "score", "health", "delta", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), -100, 100),
            D("fusionRisk.mean", "Fusion risk mean", "ratio", "fusion", "mean", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 1),
            D("fusionRisk.maximum", "Fusion risk maximum", "ratio", "fusion", "maximum", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 1),
            D("grade.changeCount", "Grade change count", "count", "health", "count", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100000),
            D("grade.degradedOrWorseRatio", "Degraded-or-worse grade ratio", "ratio", "health", "ratio", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 1),
            D("grade.criticalRatio", "Critical grade ratio", "ratio", "health", "ratio", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 1),
            D("history.sampleCount", "Retained history sample count", "count", "health", "count", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 100000),
            D("history.spanHours", "Retained history span", "hours", "health", "span", TimeSpan.FromHours(168), TimeSpan.FromMinutes(5), 0, 175200)
        };
    }

    public static FeatureSchema CreateApprovedSchema(string approvedBy, DateTimeOffset approvedAtUtc)
    {
        var definitions = CreateDefinitions();
        var items = definitions.Select((definition, ordinal) =>
            new FeatureSchemaItem(definition.FeatureId, definition.DefinitionHash, ordinal));
        return FeatureSchema.Create(SchemaId, Version, items).Approve(approvedBy, approvedAtUtc);
    }

    public static bool MatchesModelManifest(
        FeatureSchema schema,
        string modelFeatureSchemaId,
        string modelFeatureSchemaHash)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Status != FeatureSchemaStatus.Approved) return false;
        if (string.IsNullOrWhiteSpace(modelFeatureSchemaId) || string.IsNullOrWhiteSpace(modelFeatureSchemaHash)) return false;
        return string.Equals(schema.SchemaId, modelFeatureSchemaId.Trim(), StringComparison.Ordinal) &&
               string.Equals(schema.SchemaHash, modelFeatureSchemaHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static FeatureDefinition D(
        string id,
        string name,
        string unit,
        string source,
        string aggregation,
        TimeSpan window,
        TimeSpan freshness,
        double min,
        double max) =>
        FeatureDefinition.Create(
            id,
            name,
            "Asset",
            FeatureDataType.Double,
            unit,
            source,
            aggregation,
            window,
            freshness,
            FeatureNullPolicy.Fail,
            null,
            min,
            max,
            Version,
            "WCS AnomalyEngine v3.9");
}
