namespace Wcs.Desktop.Services;

using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Wcs.Core.TransportScheduling;

public interface ITransportResilienceApiService
{
    Task<TransportResilienceSnapshot?> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<TransportReadinessReport?> RunReadinessAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportOperationalBaseline>> GetBaselinesAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportLogicalBackupManifest>> GetBackupsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task<TransportBackupValidationReport?> ValidateBackupAsync(string backupId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportRecoveryDrillReport>> GetDrillsAsync(int maxCount = 100, CancellationToken cancellationToken = default);
}

public sealed class TransportResilienceApiService : ITransportResilienceApiService
{
    private readonly HttpClient _http;

    public TransportResilienceApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<TransportResilienceSnapshot?> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<TransportResilienceSnapshot>(
            "/api/transport/resilience/summary",
            cancellationToken);

    public async Task<TransportReadinessReport?> RunReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync(
            "/api/transport/resilience/readiness/run",
            null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransportReadinessReport>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TransportOperationalBaseline>> GetBaselinesAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportOperationalBaseline>>(
            $"/api/transport/resilience/baselines?maxCount={Math.Clamp(maxCount, 1, 100)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TransportLogicalBackupManifest>> GetBackupsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportLogicalBackupManifest>>(
            $"/api/transport/resilience/backups?maxCount={Math.Clamp(maxCount, 1, 1000)}",
            cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<TransportBackupValidationReport?> ValidateBackupAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync(
            $"/api/transport/resilience/backups/{Uri.EscapeDataString(backupId)}/validate",
            null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransportBackupValidationReport>(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TransportRecoveryDrillReport>> GetDrillsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<List<TransportRecoveryDrillReport>>(
            $"/api/transport/resilience/drills?maxCount={Math.Clamp(maxCount, 1, 100)}",
            cancellationToken).ConfigureAwait(false) ?? [];
}
