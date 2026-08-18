namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Wcs.Optimization;

public interface IDigitalTwinOptimizerApiService
{
    Task<DigitalTwinOptimizerStatusDto?> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OptimizationExperimentSummary>> ListExperimentsAsync(int limit = 100, CancellationToken ct = default);
    Task<OptimizationExperimentResult?> GetResultAsync(string experimentId, CancellationToken ct = default);
}

public sealed class DigitalTwinOptimizerApiService : IDigitalTwinOptimizerApiService
{
    private readonly HttpClient _http;

    public DigitalTwinOptimizerApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<DigitalTwinOptimizerStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<DigitalTwinOptimizerStatusDto>("/api/digital-twin-optimizer/status", ct);

    public async Task<IReadOnlyList<OptimizationExperimentSummary>> ListExperimentsAsync(int limit = 100, CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var envelope = await _http.GetFromJsonAsync<DigitalTwinOptimizerExperimentListEnvelope>(
            $"/api/digital-twin-optimizer/experiments?limit={bounded}", ct);
        return envelope?.Values ?? [];
    }

    public async Task<OptimizationExperimentResult?> GetResultAsync(string experimentId, CancellationToken ct = default)
    {
        var envelope = await _http.GetFromJsonAsync<DigitalTwinOptimizerResultEnvelope>(
            $"/api/digital-twin-optimizer/experiments/{Uri.EscapeDataString(experimentId)}/result", ct);
        return envelope?.Value;
    }
}

public sealed class DigitalTwinOptimizerStatusDto
{
    public string Stage { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string MaximumAutomationLevel { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public bool AutoProductionPolicyReplacementAllowed { get; init; }
    public bool ProductionAutomationAllowed { get; init; }
    public bool ExecutionApiExposed { get; init; }
    public string Persistence { get; init; } = string.Empty;
    public int DeterminismRoundsPerInput { get; init; }
    public IReadOnlyList<string> RequiredSimulationStages { get; init; } = [];
    public IReadOnlyList<string> RequiredLoadCases { get; init; } = [];
    public OptimizationRecoveryResult Recovery { get; init; } = new(0, 0, 0, false, []);
}

public sealed class DigitalTwinOptimizerExperimentListEnvelope
{
    public IReadOnlyList<OptimizationExperimentSummary> Values { get; init; } = [];
}

public sealed class DigitalTwinOptimizerResultEnvelope
{
    public OptimizationExperimentResult? Value { get; init; }
}
