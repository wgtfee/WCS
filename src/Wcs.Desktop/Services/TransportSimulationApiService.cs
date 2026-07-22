namespace Wcs.Desktop.Services;

using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Wcs.Core.TransportScheduling;

public interface ITransportSimulationApiService
{
    Task<TransportSimulationSummary?> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportSimulationRun>> GetRunsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportStrategyComparisonReport>> GetComparisonsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportBatchOptimizationResult>> GetOptimizationsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportCapacityBenchmarkReport>> GetBenchmarksAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportFinalAcceptanceReport>> GetAcceptanceReportsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
}

public sealed class TransportSimulationApiService : ITransportSimulationApiService
{
    private readonly HttpClient _http;

    public TransportSimulationApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<TransportSimulationSummary?> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<TransportSimulationSummary>(
            "/api/transport/simulation/summary",
            cancellationToken);

    public async Task<IReadOnlyList<TransportSimulationRun>> GetRunsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportSimulationRun>>(
            $"/api/transport/simulation/runs?maxCount={Math.Clamp(maxCount, 1, 500)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TransportStrategyComparisonReport>> GetComparisonsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportStrategyComparisonReport>>(
            $"/api/transport/simulation/comparisons?maxCount={Math.Clamp(maxCount, 1, 500)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TransportBatchOptimizationResult>> GetOptimizationsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportBatchOptimizationResult>>(
            $"/api/transport/simulation/optimizations?maxCount={Math.Clamp(maxCount, 1, 500)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TransportCapacityBenchmarkReport>> GetBenchmarksAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportCapacityBenchmarkReport>>(
            $"/api/transport/simulation/capacity-benchmarks?maxCount={Math.Clamp(maxCount, 1, 500)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TransportFinalAcceptanceReport>> GetAcceptanceReportsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportFinalAcceptanceReport>>(
            $"/api/transport/simulation/acceptance-reports?maxCount={Math.Clamp(maxCount, 1, 500)}",
            cancellationToken).ConfigureAwait(false) ?? [];
}
