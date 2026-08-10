namespace Wcs.Desktop.Services;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

public interface ISimulationInspectionApiService
{
    Task<IReadOnlyList<SimulationInspectionItemDto>> GetStageInspectionAsync(
        string stageId,
        SimulationInspectionView view,
        Guid? runId,
        CancellationToken cancellationToken = default);
}

public enum SimulationInspectionView
{
    Status,
    Primary,
    Secondary,
    Audit
}

/// <summary>
/// Read-only client for the existing S2-S8 inspection endpoints.
/// It intentionally contains no POST/PUT/PATCH/DELETE operation and cannot mutate
/// virtual PLC/RGV/Traffic/External/Health/Integration state.
/// </summary>
public sealed class SimulationInspectionApiService : ISimulationInspectionApiService
{
    private readonly HttpClient _http;

    public SimulationInspectionApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<IReadOnlyList<SimulationInspectionItemDto>> GetStageInspectionAsync(
        string stageId,
        SimulationInspectionView view,
        Guid? runId,
        CancellationToken cancellationToken = default)
    {
        var path = BuildPath(stageId, view, runId);
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("当前环境、RunId 或阶段检查资源不可用。Production/未授权环境会按设计返回 404。");

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var errorDocument = JsonDocument.Parse(body);
                    if (errorDocument.RootElement.TryGetProperty("error", out var error))
                        throw new InvalidOperationException(error.GetString() ?? "阶段检查失败。");
                }
                catch (JsonException)
                {
                    // Fall through to the HTTP status below.
                }
            }

            throw new HttpRequestException(
                $"Simulation inspection failed: {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Flatten(document.RootElement);
    }

    private static string BuildPath(string stageId, SimulationInspectionView view, Guid? runId)
    {
        if (string.Equals(stageId, "S8", StringComparison.OrdinalIgnoreCase))
            return "/api/simulation/capacity-readiness/status";

        if (runId is null)
            throw new InvalidOperationException($"{stageId} 检查需要先选择一个非终态 Run。 ");

        var id = runId.Value.ToString("D");
        return stageId.ToUpperInvariant() switch
        {
            "S2" => $"/api/simulation/virtual-plc/runs/{id}/{Select(view, "status", "blocks", "faults", "audit")}",
            "S3" => $"/api/simulation/virtual-rgv/runs/{id}/{Select(view, "status", "vehicles", "occupancy", "audit")}",
            "S4" => $"/api/simulation/virtual-traffic/runs/{id}/{Select(view, "status", "reservations", "deadlocks", "audit")}",
            "S5" => $"/api/simulation/virtual-external/runs/{id}/{Select(view, "status", "requests", "faults", "audit")}",
            "S6" => $"/api/simulation/virtual-health/runs/{id}/{Select(view, "status", "assets", "audit", "audit")}",
            "S7" => $"/api/simulation/virtual-integration/runs/{id}/{Select(view, "status", "missions", "audit", "audit")}",
            _ => throw new InvalidOperationException("分层检查仅支持 S2～S8。S0/S1 使用治理与 Run 控制页，S9 保持真实 HIL 只读边界。")
        };
    }

    private static string Select(
        SimulationInspectionView view,
        string status,
        string primary,
        string secondary,
        string audit) => view switch
    {
        SimulationInspectionView.Status => status,
        SimulationInspectionView.Primary => primary,
        SimulationInspectionView.Secondary => secondary,
        SimulationInspectionView.Audit => audit,
        _ => status
    };

    private static IReadOnlyList<SimulationInspectionItemDto> Flatten(JsonElement root)
    {
        var result = new List<SimulationInspectionItemDto>();
        Visit(root, "$", result, 0);
        return result;
    }

    private static void Visit(
        JsonElement element,
        string path,
        ICollection<SimulationInspectionItemDto> result,
        int depth)
    {
        if (result.Count >= 2_000)
            return;
        if (depth > 16)
        {
            result.Add(new(path, "<maximum depth reached>"));
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToArray();
                if (properties.Length == 0)
                    result.Add(new(path, "{}"));
                foreach (var property in properties)
                    Visit(property.Value, $"{path}.{property.Name}", result, depth + 1);
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= 500 || result.Count >= 2_000)
                    {
                        result.Add(new($"{path}[...]", "<truncated>"));
                        break;
                    }
                    Visit(item, $"{path}[{index}]", result, depth + 1);
                    index++;
                }
                if (index == 0)
                    result.Add(new(path, "[]"));
                break;

            case JsonValueKind.String:
                result.Add(new(path, element.GetString() ?? string.Empty));
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                result.Add(new(path, "null"));
                break;

            default:
                result.Add(new(path, element.GetRawText()));
                break;
        }
    }
}

public sealed record SimulationInspectionItemDto(string Path, string Value);
