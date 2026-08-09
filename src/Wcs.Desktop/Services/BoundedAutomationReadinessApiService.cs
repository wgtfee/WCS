namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Wcs.IndustrialIntelligence.Governance;

public interface IBoundedAutomationReadinessApiService
{
    Task<BoundedAutomationReadinessStatusDto?> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetProhibitionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BoundedAutomationReadinessEvidenceRecord>> ListEvidenceAsync(int limit = 100, CancellationToken ct = default);
    Task<BoundedAutomationReadinessEvidenceRecord?> GetEvidenceAsync(string evaluationId, CancellationToken ct = default);
}

public sealed class BoundedAutomationReadinessApiService : IBoundedAutomationReadinessApiService
{
    private readonly HttpClient _http;

    public BoundedAutomationReadinessApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<BoundedAutomationReadinessStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<BoundedAutomationReadinessStatusDto>("/api/bounded-automation-readiness/status", ct);

    public async Task<IReadOnlyList<string>> GetProhibitionsAsync(CancellationToken ct = default)
    {
        var envelope = await _http.GetFromJsonAsync<BoundedAutomationProhibitionsEnvelope>(
            "/api/bounded-automation-readiness/prohibitions", ct);
        return envelope?.Values ?? [];
    }

    public async Task<IReadOnlyList<BoundedAutomationReadinessEvidenceRecord>> ListEvidenceAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var envelope = await _http.GetFromJsonAsync<BoundedAutomationEvidenceListEnvelope>(
            $"/api/bounded-automation-readiness/evidence?limit={bounded}", ct);
        return envelope?.Values ?? [];
    }

    public async Task<BoundedAutomationReadinessEvidenceRecord?> GetEvidenceAsync(
        string evaluationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evaluationId)) return null;
        var envelope = await _http.GetFromJsonAsync<BoundedAutomationEvidenceEnvelope>(
            $"/api/bounded-automation-readiness/evidence/{Uri.EscapeDataString(evaluationId.Trim())}", ct);
        return envelope?.Value;
    }
}

public sealed class BoundedAutomationReadinessStatusDto
{
    public string Stage { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string HostMaximumAutomationLevel { get; init; } = string.Empty;
    public string FinalClaim { get; init; } = string.Empty;
    public bool DefaultsDisabled { get; init; }
    public bool ProductionEnablementAllowed { get; init; }
    public bool ControlWriteAllowed { get; init; }
    public bool ExecutionApiExposed { get; init; }
    public bool ApprovalApiExposed { get; init; }
    public bool RollbackExecutionApiExposed { get; init; }
    public bool RealSiteEvidenceRequiredForL2L3 { get; init; }
    public bool RealHilEvidenceRequiredForL2L3 { get; init; }
    public bool IndependentSafetyApprovalRequiredForL2L3 { get; init; }
    public int PermanentProhibitionCount { get; init; }
    public string Persistence { get; init; } = string.Empty;
}

public sealed class BoundedAutomationProhibitionsEnvelope
{
    public IReadOnlyList<string> Values { get; init; } = [];
}

public sealed class BoundedAutomationEvidenceListEnvelope
{
    public IReadOnlyList<BoundedAutomationReadinessEvidenceRecord> Values { get; init; } = [];
}

public sealed class BoundedAutomationEvidenceEnvelope
{
    public BoundedAutomationReadinessEvidenceRecord? Value { get; init; }
}
