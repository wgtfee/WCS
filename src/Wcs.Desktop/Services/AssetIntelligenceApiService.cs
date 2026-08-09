namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Text.Json;

public interface IAssetIntelligenceApiService
{
    Task<AssetIntelligenceSnapshotDto> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed class AssetIntelligenceApiService : IAssetIntelligenceApiService
{
    private readonly HttpClient _http;

    public AssetIntelligenceApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<AssetIntelligenceSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
    {
        var healthTask = GetArrayAsync("/api/anomaly/health/assets?maxCount=200", ct);
        var rootCauseTask = GetArrayAsync("/api/anomaly/root-cause/analyses?maxCount=200", ct);
        var maintenanceTask = GetArrayAsync("/api/anomaly/maintenance/recommendations?maxCount=200", ct);
        var forecastTask = GetArrayAsync("/api/anomaly/forecast/forecasts?maxCount=200", ct);
        await Task.WhenAll(healthTask, rootCauseTask, maintenanceTask, forecastTask);

        return new AssetIntelligenceSnapshotDto(
            healthTask.Result.Select(ParseHealth).ToArray(),
            rootCauseTask.Result.Select(ParseRootCause).ToArray(),
            maintenanceTask.Result.Select(ParseMaintenance).ToArray(),
            forecastTask.Result.Select(ParseForecast).ToArray());
    }

    private async Task<IReadOnlyList<JsonElement>> GetArrayAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        catch { return []; }
    }

    private static AssetHealthRowDto ParseHealth(JsonElement x) => new(
        Text(x, "assetId"),
        Number(x, "healthScore"),
        EnumText(x, "grade", ["Healthy", "Attention", "Degraded", "Critical"]),
        Number(x, "fusionRiskScore"),
        Int(x, "independentSourceCount"),
        Text(x, "summary"),
        Date(x, "calculatedAtUtc"));

    private static RootCauseRowDto ParseRootCause(JsonElement x)
    {
        var primary = x.TryGetProperty("primaryCandidate", out var candidate) && candidate.ValueKind == JsonValueKind.Object
            ? Text(candidate, "nodeId")
            : string.Empty;
        return new RootCauseRowDto(
            Text(x, "analysisId"),
            Text(x, "triggerEventId"),
            primary,
            EnumText(x, "reviewDecision", ["Pending", "Confirmed", "Rejected", "Supplemented"]),
            Date(x, "analyzedAtUtc"));
    }

    private static MaintenanceRowDto ParseMaintenance(JsonElement x) => new(
        Text(x, "recommendationId"),
        Text(x, "assetId"),
        EnumText(x, "status", ["Proposed", "Accepted", "Rejected", "InProgress", "Completed", "Cancelled"]),
        Text(x, "title"),
        Text(x, "mesWorkOrderNo"),
        Date(x, "createdAtUtc"));

    private static ForecastRowDto ParseForecast(JsonElement x) => new(
        Text(x, "forecastId"),
        Text(x, "assetId"),
        Number(x, "failureProbability24Hours"),
        Number(x, "failureProbability72Hours"),
        Number(x, "failureProbability168Hours"),
        Number(x, "rulMedianHours"),
        Text(x, "modelVersion"),
        Date(x, "forecastedAtUtc"));

    private static string Text(JsonElement x, string name)
    {
        if (!x.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static string EnumText(JsonElement x, string name, IReadOnlyList<string> names)
    {
        if (!x.TryGetProperty(name, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ordinal) && ordinal >= 0 && ordinal < names.Count)
            return names[ordinal];
        return value.ToString();
    }

    private static double Number(JsonElement x, string name) =>
        x.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static int Int(JsonElement x, string name) =>
        x.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? Date(JsonElement x, string name) =>
        x.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;
}

public sealed record AssetIntelligenceSnapshotDto(
    IReadOnlyList<AssetHealthRowDto> Health,
    IReadOnlyList<RootCauseRowDto> RootCauses,
    IReadOnlyList<MaintenanceRowDto> Maintenance,
    IReadOnlyList<ForecastRowDto> Forecasts);

public sealed record AssetHealthRowDto(string AssetId, double HealthScore, string Grade, double FusionRiskScore, int IndependentSourceCount, string Summary, DateTimeOffset? CalculatedAtUtc);
public sealed record RootCauseRowDto(string AnalysisId, string TriggerEventId, string PrimaryNodeId, string ReviewDecision, DateTimeOffset? CreatedAtUtc);
public sealed record MaintenanceRowDto(string RecommendationId, string AssetId, string Status, string Summary, string MesWorkOrderNo, DateTimeOffset? CreatedAtUtc);
public sealed record ForecastRowDto(string ForecastId, string AssetId, double P24, double P72, double P168, double RulMedianHours, string ModelVersion, DateTimeOffset? CreatedAtUtc);
