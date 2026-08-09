namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;

public interface IFeatureCenterApiService
{
    Task<IReadOnlyList<FeatureDefinitionDto>> GetFeaturesAsync(CancellationToken ct = default);
    Task<FeatureSchemaDto?> GetSchemaAsync(string schemaId, string version, CancellationToken ct = default);
    Task<FeatureSnapshotDto?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default);
    Task<FeatureDatasetManifestDto?> GetDatasetAsync(string datasetId, string version, CancellationToken ct = default);
}

public sealed class FeatureCenterApiService : IFeatureCenterApiService
{
    private readonly HttpClient _http;

    public FeatureCenterApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<IReadOnlyList<FeatureDefinitionDto>> GetFeaturesAsync(CancellationToken ct = default)
    {
        var envelope = await _http.GetFromJsonAsync<FeatureListEnvelope>("/api/industrial-intelligence/features", ct);
        return envelope?.Values ?? [];
    }

    public async Task<FeatureSchemaDto?> GetSchemaAsync(string schemaId, string version, CancellationToken ct = default)
    {
        var path = $"/api/industrial-intelligence/feature-schemas/{Uri.EscapeDataString(schemaId)}/{Uri.EscapeDataString(version)}";
        var envelope = await _http.GetFromJsonAsync<FeatureSchemaEnvelope>(path, ct);
        return envelope?.Value;
    }

    public async Task<FeatureSnapshotDto?> GetSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        var path = $"/api/industrial-intelligence/feature-snapshots/{Uri.EscapeDataString(snapshotId)}";
        var envelope = await _http.GetFromJsonAsync<FeatureSnapshotEnvelope>(path, ct);
        return envelope?.Value;
    }

    public async Task<FeatureDatasetManifestDto?> GetDatasetAsync(string datasetId, string version, CancellationToken ct = default)
    {
        var path = $"/api/industrial-intelligence/datasets/{Uri.EscapeDataString(datasetId)}/{Uri.EscapeDataString(version)}";
        var envelope = await _http.GetFromJsonAsync<FeatureDatasetEnvelope>(path, ct);
        return envelope?.Value;
    }
}

public enum FeatureDataTypeDto { Boolean = 0, Int64 = 1, Double = 2, String = 3 }
public enum FeatureNullPolicyDto { Fail = 0, Default = 1, Ignore = 2 }
public enum FeatureQualityStatusDto { Valid = 0, Stale = 1, Missing = 2, OutOfRange = 3, Invalid = 4 }
public enum FeatureSchemaStatusDto { Draft = 0, Approved = 1, Retired = 2 }

public sealed class FeatureListEnvelope { public IReadOnlyList<FeatureDefinitionDto> Values { get; init; } = []; }
public sealed class FeatureSchemaEnvelope { public FeatureSchemaDto? Value { get; init; } }
public sealed class FeatureSnapshotEnvelope { public FeatureSnapshotDto? Value { get; init; } }
public sealed class FeatureDatasetEnvelope { public FeatureDatasetManifestDto? Value { get; init; } }

public sealed class FeatureDefinitionDto
{
    public string FeatureId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public FeatureDataTypeDto DataType { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Aggregation { get; init; } = string.Empty;
    public TimeSpan Window { get; init; }
    public TimeSpan Freshness { get; init; }
    public FeatureNullPolicyDto NullPolicy { get; init; }
    public string Version { get; init; } = string.Empty;
    public string DefinitionHash { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
}

public sealed class FeatureSchemaDto
{
    public string SchemaId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SchemaHash { get; init; } = string.Empty;
    public FeatureSchemaStatusDto Status { get; init; }
    public string ApprovedBy { get; init; } = string.Empty;
    public DateTimeOffset? ApprovedAtUtc { get; init; }
    public IReadOnlyList<FeatureSchemaItemDto> Items { get; init; } = [];
}

public sealed class FeatureSchemaItemDto
{
    public string FeatureId { get; init; } = string.Empty;
    public string DefinitionHash { get; init; } = string.Empty;
    public int Ordinal { get; init; }
}

public sealed class FeatureSnapshotDto
{
    public string SnapshotId { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public DateTimeOffset AsOfUtc { get; init; }
    public string FeatureSchemaId { get; init; } = string.Empty;
    public string FeatureSchemaHash { get; init; } = string.Empty;
    public string ValuesHash { get; init; } = string.Empty;
    public FeatureQualityStatusDto QualityStatus { get; init; }
    public string MaterializerVersion { get; init; } = string.Empty;
    public IReadOnlyList<FeatureValueDto> Values { get; init; } = [];
}

public sealed class FeatureValueDto
{
    public string FeatureId { get; init; } = string.Empty;
    public object? Value { get; init; }
    public FeatureQualityStatusDto QualityStatus { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
}

public sealed class FeatureDatasetManifestDto
{
    public string DatasetId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FeatureSchemaId { get; init; } = string.Empty;
    public string FeatureSchemaHash { get; init; } = string.Empty;
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public long RowCount { get; init; }
    public string DatasetHash { get; init; } = string.Empty;
    public string StorageUri { get; init; } = string.Empty;
    public string StorageSha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}
