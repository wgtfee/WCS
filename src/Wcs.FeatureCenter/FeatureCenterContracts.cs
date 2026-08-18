namespace Wcs.FeatureCenter;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public enum FeatureDataType { Boolean, Int64, Double, String }
public enum FeatureNullPolicy { Fail, Default, Ignore }
public enum FeatureQualityStatus { Valid, Stale, Missing, OutOfRange, Invalid }
public enum FeatureSchemaStatus { Draft, Approved, Retired }

public sealed record FeatureDefinition(
    string FeatureId,
    string Name,
    string EntityType,
    FeatureDataType DataType,
    string Unit,
    string Source,
    string Aggregation,
    TimeSpan Window,
    TimeSpan Freshness,
    FeatureNullPolicy NullPolicy,
    string? DefaultValue,
    double? Minimum,
    double? Maximum,
    string Version,
    string DefinitionHash,
    string Owner)
{
    public static FeatureDefinition Create(
        string featureId, string name, string entityType, FeatureDataType dataType,
        string unit, string source, string aggregation, TimeSpan window, TimeSpan freshness,
        FeatureNullPolicy nullPolicy, string? defaultValue, double? minimum, double? maximum,
        string version, string owner)
    {
        var candidate = new FeatureDefinition(
            Require(featureId, nameof(featureId)), Require(name, nameof(name)), Require(entityType, nameof(entityType)), dataType,
            unit?.Trim() ?? string.Empty, Require(source, nameof(source)), Require(aggregation, nameof(aggregation)),
            PositiveOrZero(window, nameof(window)), Positive(freshness, nameof(freshness)), nullPolicy,
            string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim(), minimum, maximum,
            Require(version, nameof(version)), string.Empty, Require(owner, nameof(owner)));
        ValidateRange(candidate.Minimum, candidate.Maximum);
        return candidate with { DefinitionHash = FeatureDefinitionHash.Compute(candidate) };
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();
    private static TimeSpan Positive(TimeSpan value, string name) => value <= TimeSpan.Zero ? throw new ArgumentOutOfRangeException(name) : value;
    private static TimeSpan PositiveOrZero(TimeSpan value, string name) => value < TimeSpan.Zero ? throw new ArgumentOutOfRangeException(name) : value;
    private static void ValidateRange(double? min, double? max)
    {
        if (min is not null && max is not null && min > max) throw new ArgumentException("Minimum must be <= Maximum.");
    }
}

public static class FeatureDefinitionHash
{
    public static string Compute(FeatureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var canonical = string.Join("\n",
            definition.FeatureId.Trim(), definition.Name.Trim(), definition.EntityType.Trim(), definition.DataType,
            definition.Unit.Trim(), definition.Source.Trim(), definition.Aggregation.Trim(),
            definition.Window.Ticks.ToString(CultureInfo.InvariantCulture), definition.Freshness.Ticks.ToString(CultureInfo.InvariantCulture),
            definition.NullPolicy, definition.DefaultValue ?? string.Empty,
            definition.Minimum?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            definition.Maximum?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            definition.Version.Trim(), definition.Owner.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record FeatureSchemaItem(string FeatureId, string DefinitionHash, int Ordinal);

public sealed record FeatureSchema(
    string SchemaId,
    string Version,
    string SchemaHash,
    FeatureSchemaStatus Status,
    IReadOnlyList<FeatureSchemaItem> Items,
    string ApprovedBy,
    DateTimeOffset? ApprovedAtUtc)
{
    public static FeatureSchema Create(string schemaId, string version, IEnumerable<FeatureSchemaItem> items)
    {
        var ordered = items?.OrderBy(x => x.Ordinal).ToArray() ?? throw new ArgumentNullException(nameof(items));
        if (ordered.Length == 0) throw new ArgumentException("Feature schema must contain at least one item.", nameof(items));
        if (ordered.Select(x => x.Ordinal).Distinct().Count() != ordered.Length) throw new ArgumentException("Feature schema ordinals must be unique.");
        if (ordered.Select(x => x.FeatureId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length) throw new ArgumentException("Feature schema feature ids must be unique.");
        for (var i = 0; i < ordered.Length; i++) if (ordered[i].Ordinal != i) throw new ArgumentException("Feature schema ordinals must be contiguous and zero-based.");
        var draft = new FeatureSchema(Require(schemaId), Require(version), string.Empty, FeatureSchemaStatus.Draft, ordered, string.Empty, null);
        return draft with { SchemaHash = FeatureSchemaHash.Compute(draft) };
    }

    public FeatureSchema Approve(string actor, DateTimeOffset approvedAtUtc) => this with
    {
        Status = FeatureSchemaStatus.Approved,
        ApprovedBy = Require(actor),
        ApprovedAtUtc = approvedAtUtc
    };

    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
}

public static class FeatureSchemaHash
{
    public static string Compute(FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder().Append(schema.SchemaId.Trim()).Append('\n').Append(schema.Version.Trim()).Append('\n');
        foreach (var item in schema.Items.OrderBy(x => x.Ordinal))
            sb.Append(item.Ordinal).Append('|').Append(item.FeatureId.Trim()).Append('|').Append(item.DefinitionHash.Trim().ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

public sealed record FeatureValue(string FeatureId, object? Value, FeatureQualityStatus QualityStatus, DateTimeOffset ObservedAtUtc);
public sealed record FeatureSourceOffset(string Source, string Offset, DateTimeOffset ObservedAtUtc);
public sealed record FeatureSnapshot(
    string SnapshotId,
    string EntityId,
    DateTimeOffset AsOfUtc,
    string FeatureSchemaId,
    string FeatureSchemaHash,
    IReadOnlyList<FeatureValue> Values,
    IReadOnlyList<FeatureSourceOffset> SourceOffsets,
    string ValuesHash,
    FeatureQualityStatus QualityStatus,
    string MaterializerVersion);

public static class FeatureSnapshotHash
{
    public static string ComputeValuesHash(IEnumerable<FeatureValue> values)
    {
        var canonical = values.OrderBy(x => x.FeatureId, StringComparer.Ordinal).Select(x => new
        {
            x.FeatureId,
            Value = Normalize(x.Value),
            Quality = x.QualityStatus.ToString(),
            ObservedAtUtc = x.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string Normalize(object? value) => value switch
    {
        null => "<null>",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}

public sealed record FeatureQualityEvent(
    string QualityEventId, string EntityId, string FeatureId, FeatureQualityStatus Status,
    string Reason, DateTimeOffset OccurredAtUtc, string EvidenceSha256, string CorrelationId);

public sealed record FeatureLineageEntry(
    string LineageId, string OutputId, string OutputType, string SourceType, string SourceId,
    DateTimeOffset AsOfUtc, string TransformationVersion, string CorrelationId);

public sealed record FeatureDatasetRow(string EntityId, DateTimeOffset AsOfUtc, IReadOnlyDictionary<string, object?> Values, DateTimeOffset? OutcomeAtUtc = null);

public sealed record FeatureDatasetManifest(
    string DatasetId, string Version, string FeatureSchemaId, string FeatureSchemaHash,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, long RowCount, string DatasetHash,
    string StorageUri, string StorageSha256, DateTimeOffset CreatedAtUtc, string CreatedBy, string CorrelationId);

public static class PointInTimeRules
{
    public static void ValidateRow(FeatureDatasetRow row, IEnumerable<FeatureValue> sourceValues)
    {
        foreach (var value in sourceValues)
            if (value.ObservedAtUtc > row.AsOfUtc)
                throw new InvalidOperationException($"Feature '{value.FeatureId}' observed after AsOfUtc would leak future data.");
        if (row.OutcomeAtUtc is not null && row.OutcomeAtUtc <= row.AsOfUtc)
            throw new InvalidOperationException("OutcomeAtUtc must be after AsOfUtc for predictive datasets.");
    }
}

public sealed class FeatureCenterLimits
{
    public int MaximumDefinitions { get; init; } = 10_000;
    public int MaximumSchemaItems { get; init; } = 256;
    public int MaximumEntities { get; init; } = 100_000;
    public int MaximumSnapshotsPerQuery { get; init; } = 10_000;
    public long MaximumDatasetRows { get; init; } = 5_000_000;
    public int SnapshotRetentionDays { get; init; } = 90;

    public void Validate()
    {
        if (MaximumDefinitions is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumDefinitions));
        if (MaximumSchemaItems is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(MaximumSchemaItems));
        if (MaximumEntities is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumEntities));
        if (MaximumSnapshotsPerQuery is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumSnapshotsPerQuery));
        if (MaximumDatasetRows is < 1 or > 50_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumDatasetRows));
        if (SnapshotRetentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(SnapshotRetentionDays));
    }
}
