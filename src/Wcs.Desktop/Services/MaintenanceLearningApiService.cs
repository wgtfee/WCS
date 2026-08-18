namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Wcs.MaintenanceLearning;

public interface IMaintenanceLearningApiService
{
    Task<MaintenanceLearningStatusDto?> GetStatusAsync(CancellationToken ct = default);
    Task<MaintenanceIntervention?> GetInterventionAsync(string interventionId, CancellationToken ct = default);
    Task<IReadOnlyList<MesOutboxEntry>> GetPendingOutboxAsync(int limit = 100, CancellationToken ct = default);
}

public sealed class MaintenanceLearningApiService : IMaintenanceLearningApiService
{
    private readonly HttpClient _http;

    public MaintenanceLearningApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<MaintenanceLearningStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<MaintenanceLearningStatusDto>("/api/maintenance-learning/status", ct);

    public async Task<MaintenanceIntervention?> GetInterventionAsync(string interventionId, CancellationToken ct = default)
    {
        var dto = await _http.GetFromJsonAsync<MaintenanceInterventionEnvelope>(
            $"/api/maintenance-learning/interventions/{Uri.EscapeDataString(interventionId)}", ct);
        return dto?.Value;
    }

    public async Task<IReadOnlyList<MesOutboxEntry>> GetPendingOutboxAsync(int limit = 100, CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var dto = await _http.GetFromJsonAsync<MaintenanceOutboxEnvelope>($"/api/maintenance-learning/outbox/pending?limit={bounded}", ct);
        return dto?.Values ?? [];
    }
}

public sealed class MaintenanceLearningStatusDto
{
    public string Stage { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string MaximumAutomationLevel { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public bool AutoTrainingAllowed { get; init; }
    public bool AutoModelActivationAllowed { get; init; }
    public bool ProductionAutomationAllowed { get; init; }
    public string Persistence { get; init; } = string.Empty;
    public MaintenanceLearningRecoverySnapshot Recovery { get; init; } = new(0, 0, 0, string.Empty);
}

public sealed class MaintenanceInterventionEnvelope
{
    public MaintenanceIntervention? Value { get; init; }
}

public sealed class MaintenanceOutboxEnvelope
{
    public IReadOnlyList<MesOutboxEntry> Values { get; init; } = [];
}
