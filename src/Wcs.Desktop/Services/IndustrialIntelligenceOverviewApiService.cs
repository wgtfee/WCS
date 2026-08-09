namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Text.Json;

public interface IIndustrialIntelligenceOverviewApiService
{
    Task<IReadOnlyList<IntelligenceStageStatusDto>> GetStagesAsync(CancellationToken ct = default);
}

public sealed class IndustrialIntelligenceOverviewApiService : IIndustrialIntelligenceOverviewApiService
{
    private readonly HttpClient _http;

    private static readonly (string Stage, string Name, string Path)[] Endpoints =
    [
        ("P0", "Governance", "/api/industrial-intelligence/status"),
        ("P1", "ModelOps Center", "/api/modelops/status"),
        ("P2", "Feature Center", "/api/industrial-intelligence/features"),
        ("P3", "Shadow Decision", "/api/industrial-intelligence/proposals?take=1"),
        ("P4", "Maintenance Learning", "/api/maintenance-learning/status"),
        ("P5", "Digital Twin Optimizer", "/api/digital-twin-optimizer/status"),
        ("P6", "Bounded Automation Readiness", "/api/bounded-automation-readiness/status")
    ];

    public IndustrialIntelligenceOverviewApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<IReadOnlyList<IntelligenceStageStatusDto>> GetStagesAsync(CancellationToken ct = default)
    {
        var tasks = Endpoints.Select(x => ReadStageAsync(x.Stage, x.Name, x.Path, ct)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<IntelligenceStageStatusDto> ReadStageAsync(string stage, string name, string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
                return new(stage, name, false, "Unavailable / fail-closed", string.Empty, string.Empty, false, 0);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var environment = Text(root, "environment");
            var mode = Text(root, "mode");
            var level = Text(root, "maximumAutomationLevel");
            if (string.IsNullOrWhiteSpace(level)) level = Text(root, "hostMaximumAutomationLevel");
            var claim = Text(root, "finalClaim");
            var control = Bool(root, "controlWriteAllowed");
            var count = root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.GetArrayLength()
                : 0;
            var detail = string.Join(" · ", new[] { environment, mode, level, claim }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(detail)) detail = "Available";
            return new(stage, name, true, detail, environment, level, control, count);
        }
        catch (Exception ex)
        {
            return new(stage, name, false, $"Failed: {ex.Message}", string.Empty, string.Empty, false, 0);
        }
    }

    private static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

public sealed record IntelligenceStageStatusDto(
    string Stage,
    string Name,
    bool Available,
    string Detail,
    string Environment,
    string MaximumAutomationLevel,
    bool ControlWriteAllowed,
    int ItemCount);
