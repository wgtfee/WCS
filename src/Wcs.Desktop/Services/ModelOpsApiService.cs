namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Wcs.ModelOps;

public interface IModelOpsApiService
{
    Task<ModelOpsStatusDto?> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AiModelDeployment>> GetDeploymentsAsync(string modelId, string assetType, string profile, CancellationToken ct = default);
    Task<IReadOnlyList<AiModelAuditEntry>> GetAuditAsync(string modelId, int limit = 100, CancellationToken ct = default);
    Task PromoteShadowAsync(ModelDeploymentRequest request, CancellationToken ct = default);
    Task PromoteChampionAsync(ModelDeploymentRequest request, CancellationToken ct = default);
    Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct = default);
    Task QuarantineAsync(ModelQuarantineRequest request, CancellationToken ct = default);
}

public sealed class ModelOpsApiService : IModelOpsApiService
{
    private readonly HttpClient _http;

    public ModelOpsApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public Task<ModelOpsStatusDto?> GetStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<ModelOpsStatusDto>("/api/modelops/status", ct);

    public async Task<IReadOnlyList<AiModelDeployment>> GetDeploymentsAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct = default)
    {
        var path = "/api/modelops/deployments" +
                   $"?modelId={Uri.EscapeDataString(modelId)}" +
                   $"&assetType={Uri.EscapeDataString(assetType)}" +
                   $"&profile={Uri.EscapeDataString(profile)}";
        var response = await _http.GetFromJsonAsync<ModelOpsDeploymentsDto>(path, ct);
        return response?.Deployments ?? [];
    }

    public async Task<IReadOnlyList<AiModelAuditEntry>> GetAuditAsync(
        string modelId,
        int limit = 100,
        CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var path = $"/api/modelops/audit?modelId={Uri.EscapeDataString(modelId)}&limit={bounded}";
        var response = await _http.GetFromJsonAsync<ModelOpsAuditDto>(path, ct);
        return response?.Entries ?? [];
    }

    public Task PromoteShadowAsync(ModelDeploymentRequest request, CancellationToken ct = default) =>
        PostAsync("/api/modelops/deployments/shadow", request, ct);

    public Task PromoteChampionAsync(ModelDeploymentRequest request, CancellationToken ct = default) =>
        PostAsync("/api/modelops/deployments/champion", request, ct);

    public Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct = default) =>
        PostAsync("/api/modelops/deployments/rollback", request, ct);

    public Task QuarantineAsync(ModelQuarantineRequest request, CancellationToken ct = default) =>
        PostAsync("/api/modelops/deployments/quarantine", request, ct);

    private async Task PostAsync<T>(string path, T request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(path, request, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class ModelOpsStatusDto
{
    public string Stage { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string MaximumAutomationLevel { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public bool AutoPromotionAllowed { get; init; }
    public bool ProductionAutomationAllowed { get; init; }
    public string Persistence { get; init; } = string.Empty;
    public bool RecoveryHealthy { get; init; }
    public IReadOnlyList<string> RecoveryErrors { get; init; } = [];
    public int ChampionCount { get; init; }
    public int FallbackCount { get; init; }
    public int ShadowCount { get; init; }
    public int QuarantinedCount { get; init; }
}

public sealed class ModelOpsDeploymentsDto
{
    public string Stage { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public IReadOnlyList<AiModelDeployment> Deployments { get; init; } = [];
}

public sealed class ModelOpsAuditDto
{
    public string Stage { get; init; } = string.Empty;
    public bool AppendOnly { get; init; }
    public bool ControlWriteAllowed { get; init; }
    public IReadOnlyList<AiModelAuditEntry> Entries { get; init; } = [];
}
