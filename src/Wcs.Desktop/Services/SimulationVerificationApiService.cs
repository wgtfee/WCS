namespace Wcs.Desktop.Services;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

public interface ISimulationVerificationApiService
{
    Task<SimulationVerificationOverviewDto?> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegisteredSimulationScenarioDto>> GetScenariosAsync(CancellationToken cancellationToken = default);
    Task<RegisteredSimulationScenarioDto> ValidateAndRegisterAsync(ValidateSimulationScenarioDto request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationRunDto>> GetRunsAsync(CancellationToken cancellationToken = default);
    Task<SimulationRunDto> CreateRunAsync(CreateSimulationRunDto request, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> StepAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> RunToCompletionAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> PauseAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> ResumeAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> AdvanceAsync(Guid runId, long targetOffsetMilliseconds, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> SetSpeedAsync(Guid runId, double speedFactor, CancellationToken cancellationToken = default);
    Task<SimulationRunDto> CancelAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationCheckpointDto> GetCheckpointAsync(Guid runId, CancellationToken cancellationToken = default);
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

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<SimulationVerificationOverviewDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RegisteredSimulationScenarioDto>> GetScenariosAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            "/api/simulation/governance/scenarios",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<List<RegisteredSimulationScenarioDto>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public Task<RegisteredSimulationScenarioDto> ValidateAndRegisterAsync(
        ValidateSimulationScenarioDto request,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<RegisteredSimulationScenarioDto>(
            "/api/simulation/governance/scenarios/validate",
            request,
            cancellationToken);

    public async Task<IReadOnlyList<SimulationRunDto>> GetRunsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            "/api/simulation/scenarios/runs",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<List<SimulationRunDto>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public Task<SimulationRunDto> CreateRunAsync(
        CreateSimulationRunDto request,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<SimulationRunDto>("/api/simulation/scenarios/runs", request, cancellationToken);

    public Task<SimulationRunDto> StepAsync(Guid runId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync<SimulationRunDto>($"/api/simulation/scenarios/runs/{runId:D}/step", cancellationToken);

    public Task<SimulationRunDto> RunToCompletionAsync(Guid runId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync<SimulationRunDto>($"/api/simulation/scenarios/runs/{runId:D}/run", cancellationToken);

    public Task<SimulationRunDto> PauseAsync(Guid runId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync<SimulationRunDto>($"/api/simulation/scenarios/runs/{runId:D}/pause", cancellationToken);

    public Task<SimulationRunDto> ResumeAsync(Guid runId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync<SimulationRunDto>($"/api/simulation/scenarios/runs/{runId:D}/resume", cancellationToken);

    public Task<SimulationRunDto> AdvanceAsync(
        Guid runId,
        long targetOffsetMilliseconds,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<SimulationRunDto>(
            $"/api/simulation/scenarios/runs/{runId:D}/advance",
            new { targetOffsetMilliseconds },
            cancellationToken);

    public Task<SimulationRunDto> SetSpeedAsync(
        Guid runId,
        double speedFactor,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<SimulationRunDto>(
            $"/api/simulation/scenarios/runs/{runId:D}/speed",
            new { speedFactor },
            cancellationToken);

    public Task<SimulationRunDto> CancelAsync(Guid runId, CancellationToken cancellationToken = default) =>
        PostEmptyAsync<SimulationRunDto>($"/api/simulation/scenarios/runs/{runId:D}/cancel", cancellationToken);

    public async Task<SimulationCheckpointDto> GetCheckpointAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"/api/simulation/scenarios/runs/{runId:D}/checkpoint",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<SimulationCheckpointDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Simulation checkpoint response was empty.");
    }

    private async Task<T> PostEmptyAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Simulation API response was empty.");
    }

    private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Simulation API response was empty.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "当前环境未开放受治理仿真 API。启用步骤（Host 端 appsettings.json）："
                + "① 配置 \"Simulator\": { \"Enabled\": true }；"
                + "② 配置 \"SimulationGovernance\": { \"Enabled\": true }；"
                + "③ 将 ASPNETCORE_ENVIRONMENT 设为 SimulationGovernance:AllowedEnvironments 之一（默认仅 Simulation/SimulationLoadTest，Production 永远禁用）。");

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? response.ReasonPhrase ?? "Simulation API failed.");
            }
            catch (JsonException)
            {
                // Fall through to the HTTP status below.
            }
        }

        throw new HttpRequestException(
            $"Simulation API failed: {(int)response.StatusCode} {response.ReasonPhrase}",
            null,
            response.StatusCode);
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

public sealed class RegisteredSimulationScenarioDto
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long Seed { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; }

    public string Identity => $"{ScenarioId}@{Version}";
}

public sealed class SimulationScenarioManifestDto
{
    public int SchemaVersion { get; set; } = 1;
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long Seed { get; set; }
    public string ScenarioFile { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; set; }
}

public sealed class ValidateSimulationScenarioDto
{
    public SimulationScenarioManifestDto Manifest { get; set; } = new();
    public string ContentBase64 { get; set; } = string.Empty;
}

public sealed class CreateSimulationRunDto
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
    public double SpeedFactor { get; set; } = 1;
    public bool StartPaused { get; set; } = true;
}

public sealed class SimulationRunDto
{
    public Guid RunId { get; set; }
    public string ScenarioId { get; set; } = string.Empty;
    public string ScenarioVersion { get; set; } = string.Empty;
    public string ScenarioManifestHash { get; set; } = string.Empty;
    public JsonElement Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public long CurrentOffsetMilliseconds { get; set; }
    public int NextTimelineIndex { get; set; }
    public int TimelineCount { get; set; }
    public int AssertionCount { get; set; }
    public string? FailureMessage { get; set; }
    public string? FinalStateHash { get; set; }
    public string? EvidenceHash { get; set; }

    public string Identity => $"{ScenarioId}@{ScenarioVersion}";
    public string StatusText => Status.ValueKind switch
    {
        JsonValueKind.String => Status.GetString() ?? "Unknown",
        JsonValueKind.Number when Status.TryGetInt32(out var value) => value switch
        {
            0 => "Created",
            1 => "Running",
            2 => "Paused",
            3 => "Completed",
            4 => "Failed",
            5 => "Cancelled",
            _ => $"Unknown({value})"
        },
        _ => "Unknown"
    };
    public string ProgressText => TimelineCount <= 0
        ? "0 / 0"
        : $"{Math.Min(NextTimelineIndex, TimelineCount)} / {TimelineCount}";
    public bool IsTerminal => StatusText is "Completed" or "Failed" or "Cancelled";
}

public sealed class SimulationCheckpointDto
{
    public string ScenarioId { get; set; } = string.Empty;
    public string ScenarioVersion { get; set; } = string.Empty;
    public string ScenarioManifestHash { get; set; } = string.Empty;
    public long Seed { get; set; }
    public long CurrentOffsetMilliseconds { get; set; }
    public int NextTimelineIndex { get; set; }
    public string StateJson { get; set; } = string.Empty;
    public List<SimulationAssertionOutcomeDto> AssertionOutcomes { get; set; } = [];
    public string CheckpointHash { get; set; } = string.Empty;
}

public sealed class SimulationAssertionOutcomeDto
{
    public string AssertionId { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Expected { get; set; } = string.Empty;
    public string Actual { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long AtMilliseconds { get; set; }

    public string ResultText => Passed ? "PASS" : "FAIL";
}
