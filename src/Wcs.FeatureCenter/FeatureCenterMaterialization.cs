namespace Wcs.FeatureCenter;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record FeatureObservation(
    string EntityId,
    string FeatureId,
    object? Value,
    DateTimeOffset ObservedAtUtc,
    string Source,
    string Offset);

public interface IFeatureRealtimeCache
{
    Task ApplyAsync(FeatureObservation observation, CancellationToken ct);
    Task<IReadOnlyList<FeatureValue>> ReadAsOfAsync(string entityId, FeatureSchema schema, DateTimeOffset asOfUtc, CancellationToken ct);
    Task<IReadOnlyList<FeatureSourceOffset>> GetSourceOffsetsAsync(string entityId, DateTimeOffset asOfUtc, CancellationToken ct);
    Task RebuildAsync(IEnumerable<FeatureObservation> observations, CancellationToken ct);
}

/// <summary>
/// Bounded, read-only feature materialization cache. It never calls control services and can be rebuilt
/// entirely from governed observations after restart.
/// </summary>
public sealed class BoundedFeatureRealtimeCache : IFeatureRealtimeCache
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FeatureObservation>> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, FeatureDefinition> _definitions;
    private readonly FeatureQualityValidator _quality = new();
    private readonly int _maximumEntities;

    public BoundedFeatureRealtimeCache(IEnumerable<FeatureDefinition> definitions, int maximumEntities = 100_000)
    {
        if (maximumEntities is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(maximumEntities));
        _maximumEntities = maximumEntities;
        _definitions = (definitions ?? throw new ArgumentNullException(nameof(definitions)))
            .ToDictionary(x => x.FeatureId, StringComparer.OrdinalIgnoreCase);
    }

    public Task ApplyAsync(FeatureObservation observation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(observation);
        var entityId = Require(observation.EntityId, nameof(observation.EntityId));
        var featureId = Require(observation.FeatureId, nameof(observation.FeatureId));
        if (!_definitions.ContainsKey(featureId)) throw new InvalidOperationException($"Feature '{featureId}' is not governed.");

        if (!_entities.TryGetValue(entityId, out var entity))
        {
            if (_entities.Count >= _maximumEntities) throw new InvalidOperationException("Feature realtime entity capacity has been reached.");
            entity = _entities.GetOrAdd(entityId, _ => new ConcurrentDictionary<string, FeatureObservation>(StringComparer.OrdinalIgnoreCase));
        }

        entity.AddOrUpdate(featureId, observation, (_, current) => observation.ObservedAtUtc >= current.ObservedAtUtc ? observation : current);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeatureValue>> ReadAsOfAsync(string entityId, FeatureSchema schema, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(schema);
        if (!_entities.TryGetValue(Require(entityId, nameof(entityId)), out var entity))
            throw new InvalidOperationException("No materialized features exist for the entity.");

        var values = new List<FeatureValue>(schema.Items.Count);
        foreach (var item in schema.Items.OrderBy(x => x.Ordinal))
        {
            if (!_definitions.TryGetValue(item.FeatureId, out var definition) ||
                !string.Equals(definition.DefinitionHash, item.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"FeatureSchema definition hash mismatch for '{item.FeatureId}'.");
            if (!entity.TryGetValue(item.FeatureId, out var observation) || observation.ObservedAtUtc > asOfUtc)
            {
                values.Add(_quality.Validate(definition, null, asOfUtc, asOfUtc));
                continue;
            }
            values.Add(_quality.Validate(definition, observation.Value, observation.ObservedAtUtc, asOfUtc));
        }
        return Task.FromResult<IReadOnlyList<FeatureValue>>(values);
    }

    public Task<IReadOnlyList<FeatureSourceOffset>> GetSourceOffsetsAsync(string entityId, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entities.TryGetValue(Require(entityId, nameof(entityId)), out var entity))
            return Task.FromResult<IReadOnlyList<FeatureSourceOffset>>([]);
        var offsets = entity.Values
            .Where(x => x.ObservedAtUtc <= asOfUtc)
            .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.ObservedAtUtc).ThenByDescending(x => x.Offset, StringComparer.Ordinal).First())
            .Select(x => new FeatureSourceOffset(x.Source, x.Offset, x.ObservedAtUtc))
            .OrderBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<FeatureSourceOffset>>(offsets);
    }

    public async Task RebuildAsync(IEnumerable<FeatureObservation> observations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observations);
        _entities.Clear();
        foreach (var observation in observations.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.EntityId, StringComparer.Ordinal).ThenBy(x => x.FeatureId, StringComparer.Ordinal))
            await ApplyAsync(observation, ct);
    }

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}

public sealed record PointInTimeDatasetRequest(
    string DatasetId,
    string Version,
    FeatureSchema Schema,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<(string EntityId, DateTimeOffset AsOfUtc, DateTimeOffset? OutcomeAtUtc)> Anchors,
    long MaximumRows,
    string StorageUri,
    string StorageSha256,
    string CreatedBy,
    string CorrelationId);

public sealed record PointInTimeDatasetBuildResult(FeatureDatasetManifest Manifest, IReadOnlyList<FeatureDatasetRow> Rows);

public sealed class PointInTimeDatasetBuilder
{
    private readonly IFeatureRealtimeCache _cache;

    public PointInTimeDatasetBuilder(IFeatureRealtimeCache cache) => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public async Task<PointInTimeDatasetBuildResult> BuildAsync(PointInTimeDatasetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Schema.Status != FeatureSchemaStatus.Approved) throw new InvalidOperationException("Dataset requires an approved FeatureSchema.");
        if (request.ToUtc < request.FromUtc) throw new InvalidOperationException("Dataset ToUtc must be >= FromUtc.");
        if (request.MaximumRows is < 1 or > 50_000_000) throw new ArgumentOutOfRangeException(nameof(request.MaximumRows));
        if (request.Anchors.Count > request.MaximumRows) throw new InvalidOperationException("Dataset row limit exceeded.");

        var rows = new List<FeatureDatasetRow>(request.Anchors.Count);
        foreach (var anchor in request.Anchors.OrderBy(x => x.AsOfUtc).ThenBy(x => x.EntityId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (anchor.AsOfUtc < request.FromUtc || anchor.AsOfUtc > request.ToUtc) throw new InvalidOperationException("Dataset anchor is outside the requested time range.");
            if (anchor.OutcomeAtUtc is not null && anchor.OutcomeAtUtc <= anchor.AsOfUtc) throw new InvalidOperationException("OutcomeAtUtc must be after AsOfUtc.");
            var values = await _cache.ReadAsOfAsync(anchor.EntityId, request.Schema, anchor.AsOfUtc, ct);
            PointInTimeRules.ValidateRow(new FeatureDatasetRow(anchor.EntityId, anchor.AsOfUtc, new Dictionary<string, object?>(), anchor.OutcomeAtUtc), values);
            rows.Add(new FeatureDatasetRow(anchor.EntityId, anchor.AsOfUtc,
                values.OrderBy(x => request.Schema.Items.Single(i => i.FeatureId.Equals(x.FeatureId, StringComparison.OrdinalIgnoreCase)).Ordinal)
                    .ToDictionary(x => x.FeatureId, x => x.Value, StringComparer.OrdinalIgnoreCase), anchor.OutcomeAtUtc));
        }

        var datasetHash = ComputeDatasetHash(request.Schema.SchemaHash, rows);
        var manifest = new FeatureDatasetManifest(
            Require(request.DatasetId), Require(request.Version), request.Schema.SchemaId, request.Schema.SchemaHash,
            request.FromUtc, request.ToUtc, rows.Count, datasetHash, Require(request.StorageUri), RequireSha(request.StorageSha256),
            DateTimeOffset.UtcNow, Require(request.CreatedBy), Require(request.CorrelationId));
        return new PointInTimeDatasetBuildResult(manifest, rows);
    }

    public static string ComputeDatasetHash(string schemaHash, IEnumerable<FeatureDatasetRow> rows)
    {
        var canonicalRows = rows.OrderBy(x => x.AsOfUtc).ThenBy(x => x.EntityId, StringComparer.Ordinal).Select(x => new
        {
            EntityId = x.EntityId.Trim(),
            AsOfUtc = x.AsOfUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            OutcomeAtUtc = x.OutcomeAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Values = x.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new { Key = v.Key, Value = Normalize(v.Value) }).ToArray()
        });
        var json = JsonSerializer.Serialize(new { SchemaHash = RequireSha(schemaHash).ToLowerInvariant(), Rows = canonicalRows });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string Normalize(object? value) => value switch
    {
        null => "<null>",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };
    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string RequireSha(string value)
    {
        var sha = Require(value);
        if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("SHA-256 must be 64 hexadecimal characters.");
        return sha;
    }
}
