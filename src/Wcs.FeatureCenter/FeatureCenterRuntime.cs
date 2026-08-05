namespace Wcs.FeatureCenter;

using System.Collections.Concurrent;

public interface IFeatureDefinitionRegistry
{
    Task RegisterAsync(FeatureDefinition definition, CancellationToken ct);
    Task<FeatureDefinition?> GetAsync(string featureId, string version, CancellationToken ct);
    Task<IReadOnlyList<FeatureDefinition>> ListAsync(CancellationToken ct);
}

public interface IFeatureSchemaRegistry
{
    Task RegisterAsync(FeatureSchema schema, CancellationToken ct);
    Task<FeatureSchema?> GetAsync(string schemaId, string version, CancellationToken ct);
}

public interface IFeatureSnapshotService
{
    Task<FeatureSnapshot> FreezeAsync(
        string entityId,
        DateTimeOffset asOfUtc,
        FeatureSchema schema,
        IReadOnlyList<FeatureValue> values,
        IReadOnlyList<FeatureSourceOffset> sourceOffsets,
        string materializerVersion,
        CancellationToken ct);
}

public sealed class InMemoryFeatureDefinitionRegistry : IFeatureDefinitionRegistry
{
    private readonly ConcurrentDictionary<string, FeatureDefinition> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maximumDefinitions;

    public InMemoryFeatureDefinitionRegistry(int maximumDefinitions = 10_000)
    {
        _maximumDefinitions = maximumDefinitions is >= 1 and <= 1_000_000
            ? maximumDefinitions
            : throw new ArgumentOutOfRangeException(nameof(maximumDefinitions));
    }

    public Task RegisterAsync(FeatureDefinition definition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(definition);
        var key = Key(definition.FeatureId, definition.Version);
        while (true)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.DefinitionHash, definition.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The same FeatureId + Version cannot be registered with a different DefinitionHash.");
                return Task.CompletedTask;
            }
            if (_items.Count >= _maximumDefinitions)
                throw new InvalidOperationException("Feature definition capacity has been reached.");
            if (_items.TryAdd(key, definition)) return Task.CompletedTask;
        }
    }

    public Task<FeatureDefinition?> GetAsync(string featureId, string version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items.TryGetValue(Key(featureId, version), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<FeatureDefinition>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FeatureDefinition>>(_items.Values
            .OrderBy(x => x.FeatureId, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .ToArray());
    }

    private static string Key(string id, string version) => $"{id.Trim()}\u001f{version.Trim()}";
}

public sealed class InMemoryFeatureSchemaRegistry : IFeatureSchemaRegistry
{
    private readonly ConcurrentDictionary<string, FeatureSchema> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFeatureDefinitionRegistry _definitions;
    private readonly int _maximumItems;

    public InMemoryFeatureSchemaRegistry(IFeatureDefinitionRegistry definitions, int maximumItems = 256)
    {
        _definitions = definitions;
        _maximumItems = maximumItems;
    }

    public async Task RegisterAsync(FeatureSchema schema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Items.Count > _maximumItems) throw new InvalidOperationException("Feature schema item capacity exceeded.");
        foreach (var item in schema.Items)
        {
            var candidates = await _definitions.ListAsync(ct);
            var definition = candidates.FirstOrDefault(x =>
                string.Equals(x.FeatureId, item.FeatureId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DefinitionHash, item.DefinitionHash, StringComparison.OrdinalIgnoreCase));
            if (definition is null) throw new InvalidOperationException($"Feature definition '{item.FeatureId}' with the required hash is not registered.");
        }

        var key = $"{schema.SchemaId.Trim()}\u001f{schema.Version.Trim()}";
        if (_items.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.SchemaHash, schema.SchemaHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The same SchemaId + Version cannot be registered with a different SchemaHash.");
            return;
        }
        if (!_items.TryAdd(key, schema)) throw new InvalidOperationException("Feature schema registration failed.");
    }

    public Task<FeatureSchema?> GetAsync(string schemaId, string version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items.TryGetValue($"{schemaId.Trim()}\u001f{version.Trim()}", out var value);
        return Task.FromResult(value);
    }
}

public sealed class FeatureSnapshotService : IFeatureSnapshotService
{
    public Task<FeatureSnapshot> FreezeAsync(
        string entityId,
        DateTimeOffset asOfUtc,
        FeatureSchema schema,
        IReadOnlyList<FeatureValue> values,
        IReadOnlyList<FeatureSourceOffset> sourceOffsets,
        string materializerVersion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Status != FeatureSchemaStatus.Approved) throw new InvalidOperationException("Only approved FeatureSchema may be used for a formal snapshot.");
        if (values.Count != schema.Items.Count) throw new InvalidOperationException("Snapshot value count must match FeatureSchema item count.");
        var byId = values.ToDictionary(x => x.FeatureId, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FeatureValue>(schema.Items.Count);
        var overall = FeatureQualityStatus.Valid;
        foreach (var item in schema.Items.OrderBy(x => x.Ordinal))
        {
            if (!byId.TryGetValue(item.FeatureId, out var value)) throw new InvalidOperationException($"Feature '{item.FeatureId}' is missing.");
            if (value.ObservedAtUtc > asOfUtc) throw new InvalidOperationException($"Feature '{item.FeatureId}' would leak future data.");
            ordered.Add(value);
            if (value.QualityStatus != FeatureQualityStatus.Valid) overall = value.QualityStatus;
        }

        var valuesHash = FeatureSnapshotHash.ComputeValuesHash(ordered);
        var snapshotId = FeatureSnapshotIdentity.Compute(entityId, asOfUtc, schema.SchemaHash, valuesHash, materializerVersion);
        return Task.FromResult(new FeatureSnapshot(
            snapshotId, entityId.Trim(), asOfUtc, schema.SchemaId, schema.SchemaHash,
            ordered, sourceOffsets.OrderBy(x => x.Source, StringComparer.Ordinal).ToArray(), valuesHash, overall, materializerVersion.Trim()));
    }
}

public static class FeatureSnapshotIdentity
{
    public static string Compute(string entityId, DateTimeOffset asOfUtc, string schemaHash, string valuesHash, string materializerVersion)
    {
        var canonical = $"{entityId.Trim()}\n{asOfUtc.ToUniversalTime():O}\n{schemaHash.Trim().ToLowerInvariant()}\n{valuesHash.Trim().ToLowerInvariant()}\n{materializerVersion.Trim()}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class FeatureQualityValidator
{
    public FeatureValue Validate(FeatureDefinition definition, object? value, DateTimeOffset observedAtUtc, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (asOfUtc - observedAtUtc > definition.Freshness)
            return new FeatureValue(definition.FeatureId, value, FeatureQualityStatus.Stale, observedAtUtc);

        if (value is null)
        {
            return definition.NullPolicy switch
            {
                FeatureNullPolicy.Fail => new FeatureValue(definition.FeatureId, null, FeatureQualityStatus.Missing, observedAtUtc),
                FeatureNullPolicy.Default => new FeatureValue(definition.FeatureId, definition.DefaultValue, FeatureQualityStatus.Valid, observedAtUtc),
                FeatureNullPolicy.Ignore => new FeatureValue(definition.FeatureId, null, FeatureQualityStatus.Valid, observedAtUtc),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        if (definition.Minimum is not null || definition.Maximum is not null)
        {
            if (!TryDouble(value, out var numeric)) return new FeatureValue(definition.FeatureId, value, FeatureQualityStatus.Invalid, observedAtUtc);
            if (definition.Minimum is not null && numeric < definition.Minimum || definition.Maximum is not null && numeric > definition.Maximum)
                return new FeatureValue(definition.FeatureId, value, FeatureQualityStatus.OutOfRange, observedAtUtc);
        }
        return new FeatureValue(definition.FeatureId, value, FeatureQualityStatus.Valid, observedAtUtc);
    }

    private static bool TryDouble(object value, out double numeric)
    {
        try { numeric = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); return double.IsFinite(numeric); }
        catch { numeric = 0; return false; }
    }
}

public static class FeatureCatalogV1
{
    public static IReadOnlyList<FeatureDefinition> CreateDefault() =>
    [
        D("health.latest", "Health latest", "score", "health", "latest", TimeSpan.Zero, TimeSpan.FromMinutes(5), 0, 100),
        D("health.mean", "Health mean", "score", "health", "mean", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 100),
        D("health.minimum", "Health minimum", "score", "health", "minimum", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 100),
        D("health.maximum", "Health maximum", "score", "health", "maximum", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 100),
        D("health.stddev", "Health stddev", "score", "health", "stddev", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 100),
        D("health.slopePerHour", "Health slope per hour", "score/hour", "health", "slope", TimeSpan.FromHours(6), TimeSpan.FromMinutes(5), -100, 100),
        D("fusionRisk.mean", "Fusion risk mean", "ratio", "fusion", "mean", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 1),
        D("fusionRisk.maximum", "Fusion risk maximum", "ratio", "fusion", "maximum", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 1),
        D("grade.changeCount", "Grade change count", "count", "health", "count", TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), 0, 100000),
        D("grade.criticalRatio", "Critical grade ratio", "ratio", "health", "ratio", TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), 0, 1),
        D("alarm.activeCount", "Active alarm count", "count", "alarm", "latest", TimeSpan.Zero, TimeSpan.FromMinutes(1), 0, 100000),
        D("task.completedCount", "Completed task count", "count", "task", "count", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 0, 1000000),
        D("vehicle.busyRatio", "Vehicle busy ratio", "ratio", "vehicle", "ratio", TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), 0, 1),
        D("vehicle.waitSeconds", "Vehicle wait seconds", "seconds", "vehicle", "mean", TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), 0, 86400),
        D("traffic.conflictCount", "Traffic conflict count", "count", "traffic", "count", TimeSpan.FromHours(1), TimeSpan.FromMinutes(2), 0, 100000),
        D("maintenance.hoursSinceLast", "Hours since last maintenance", "hours", "maintenance", "latest", TimeSpan.Zero, TimeSpan.FromHours(1), 0, 100000)
    ];

    private static FeatureDefinition D(string id, string name, string unit, string source, string aggregation, TimeSpan window, TimeSpan freshness, double min, double max) =>
        FeatureDefinition.Create(id, name, "Asset", FeatureDataType.Double, unit, source, aggregation, window, freshness, FeatureNullPolicy.Fail, null, min, max, "1.0.0", "WCS");
}
