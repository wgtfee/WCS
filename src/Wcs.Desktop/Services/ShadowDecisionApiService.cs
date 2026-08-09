namespace Wcs.Desktop.Services;

using Microsoft.Extensions.Options;
using System.Net.Http.Json;

public interface IShadowDecisionApiService
{
    Task<IReadOnlyList<DecisionProposalDto>> GetProposalsAsync(int take = 100, CancellationToken ct = default);
    Task<DecisionProposalDetailDto?> GetProposalAsync(string proposalId, CancellationToken ct = default);
    Task ApproveAsync(string proposalId, DecisionGovernanceActionDto request, CancellationToken ct = default);
    Task RejectAsync(string proposalId, DecisionGovernanceActionDto request, CancellationToken ct = default);
    Task RecordOutcomeAsync(string proposalId, DecisionOutcomeRequestDto request, CancellationToken ct = default);
}

public sealed class ShadowDecisionApiService : IShadowDecisionApiService
{
    private readonly HttpClient _http;

    public ShadowDecisionApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<IReadOnlyList<DecisionProposalDto>> GetProposalsAsync(int take = 100, CancellationToken ct = default)
    {
        var envelope = await _http.GetFromJsonAsync<DecisionProposalListEnvelope>(
            $"/api/industrial-intelligence/proposals?take={Math.Clamp(take, 1, 1000)}", ct);
        return envelope?.Values ?? [];
    }

    public async Task<DecisionProposalDetailDto?> GetProposalAsync(string proposalId, CancellationToken ct = default)
    {
        var path = $"/api/industrial-intelligence/proposals/{Uri.EscapeDataString(proposalId)}";
        return await _http.GetFromJsonAsync<DecisionProposalDetailDto>(path, ct);
    }

    public Task ApproveAsync(string proposalId, DecisionGovernanceActionDto request, CancellationToken ct = default) =>
        PostAsync($"/api/industrial-intelligence/proposals/{Uri.EscapeDataString(proposalId)}/approve", request, ct);

    public Task RejectAsync(string proposalId, DecisionGovernanceActionDto request, CancellationToken ct = default) =>
        PostAsync($"/api/industrial-intelligence/proposals/{Uri.EscapeDataString(proposalId)}/reject", request, ct);

    public Task RecordOutcomeAsync(string proposalId, DecisionOutcomeRequestDto request, CancellationToken ct = default) =>
        PostAsync($"/api/industrial-intelligence/proposals/{Uri.EscapeDataString(proposalId)}/outcome", request, ct);

    private async Task PostAsync<T>(string path, T request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(path, request, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class DecisionProposalListEnvelope
{
    public string Stage { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public bool ProductionAutomationAllowed { get; init; }
    public IReadOnlyList<DecisionProposalDto> Values { get; init; } = [];
}

public sealed class DecisionProposalDetailDto
{
    public string Stage { get; init; } = string.Empty;
    public bool ControlWriteAllowed { get; init; }
    public bool ProductionAutomationAllowed { get; init; }
    public DecisionProposalDto? Proposal { get; init; }
    public IReadOnlyList<DecisionApprovalDto> Approvals { get; init; } = [];
    public DecisionOutcomeDto? Outcome { get; init; }
}

public sealed class DecisionProposalDto
{
    public string ProposalId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public DecisionCandidateDto Candidate { get; init; } = new();
    public DecisionEvidenceDto Evidence { get; init; } = new();
    public DecisionExplanationDto Explanation { get; init; } = new();
    public IReadOnlyList<DecisionConstraintDto> Constraints { get; init; } = [];
}

public sealed class DecisionCandidateDto
{
    public string CandidateId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public decimal Score { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed class DecisionEvidenceDto
{
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string FeatureSnapshotId { get; init; } = string.Empty;
    public string FeatureSchemaHash { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;
}

public sealed class DecisionExplanationDto
{
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Factors { get; init; } = [];
    public string EvidenceHash { get; init; } = string.Empty;
}

public sealed class DecisionConstraintDto
{
    public string Code { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;
}

public sealed class DecisionApprovalDto
{
    public string ProposalId { get; init; } = string.Empty;
    public string FromStatus { get; init; } = string.Empty;
    public string ToStatus { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset Utc { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public string EntryHash { get; init; } = string.Empty;
}

public sealed class DecisionOutcomeDto
{
    public string ProposalId { get; init; } = string.Empty;
    public string OutcomeType { get; init; } = string.Empty;
    public string ActualReference { get; init; } = string.Empty;
    public decimal? ActualBenefit { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public string EvidenceHash { get; init; } = string.Empty;
}

public sealed class DecisionGovernanceActionDto
{
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed class DecisionOutcomeRequestDto
{
    public string OutcomeType { get; init; } = string.Empty;
    public string ActualReference { get; init; } = string.Empty;
    public decimal? ActualBenefit { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public string EvidenceHash { get; init; } = string.Empty;
}
