namespace Wcs.Desktop.Services;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

public interface ISimulationVerificationApiService
{
    Task<SimulationVerificationOverviewDto?> GetOverviewAsync(CancellationToken cancellationToken = default);
}

public sealed class SimulationVerificationApiService : ISimulationVerificationApiService
{
    private readonly HttpClient _http;

    public SimulationVerificationApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<SimulationVerificationOverviewDto?> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            "/api/simulation/verification/overview",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<SimulationVerificationOverviewDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class SimulationVerificationOverviewDto
{
    public string Stage { get; set; } = "S10";
    public string Environment { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public bool RemoteControlAllowed { get; set; }
    public bool SimulationInspectionAvailable { get; set; }
    public bool HilInspectionAvailable { get; set; }
    public bool RealHilExecuted { get; set; }
    public bool ProtocolValidated { get; set; }
    public bool MechanicalSafetyAccepted { get; set; }
    public bool SiteAccepted { get; set; }
    public bool RealHilEvidenceRequiredForCompletion { get; set; }
    public List<SimulationVerificationStageDto> Stages { get; set; } = [];
}

public sealed class SimulationVerificationStageDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string ApiPrefix { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public bool ReadOnlyInspection { get; set; }
    public bool RequiresRunId { get; set; }
    public bool RequiresRealHardware { get; set; }
    public string SafetyBoundary { get; set; } = string.Empty;

    public string RunIdRequirement => RequiresRunId ? "需要" : "不需要";
    public string HardwareRequirement => RequiresRealHardware ? "真实台架" : "软件/仿真";
    public string AvailabilityText => Availability == "Available" ? "可用" : "当前环境不可用";
}
