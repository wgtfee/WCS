namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.DecisionIntelligence;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;

[ApiController]
[Route("api/industrial-intelligence/proposals")]
public sealed class ShadowDecisionController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public ShadowDecisionController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DecisionProposalStatus? status = null,
        [FromQuery] ProposalType? type = null,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        if (!TryAllowP3(out var access)) return NotFound();
        if (take is < 1 or > 1000) return BadRequest("take must be in [1,1000].");
        try
        {
            var store = GetFactory().CreateStore();
            var values = await store.QueryAsync(new DecisionQuery(Type: type, Status: status, Take: take), ct);
            return Ok(new
            {
                stage = "IDI-P3",
                environment = _environment.EnvironmentName,
                mode = access.EffectiveMode.ToString(),
                maximumAutomationLevel = access.EffectiveMaximumAutomationLevel.ToString(),
                controlWriteAllowed = false,
                productionAutomationAllowed = false,
                proposalOnly = true,
                values
            });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpGet("{proposalId}")]
    public async Task<IActionResult> Get(string proposalId, CancellationToken ct)
    {
        if (!TryAllowP3(out _)) return NotFound();
        if (!ValidId(proposalId, 64)) return BadRequest("proposalId is invalid.");
        try
        {
            var store = GetFactory().CreateStore();
            var proposal = await store.GetAsync(proposalId.Trim(), ct);
            if (proposal is null) return NotFound();
            var approvals = await store.GetApprovalsAsync(proposal.ProposalId, 200, ct);
            var outcome = await store.GetOutcomeAsync(proposal.ProposalId, ct);
            return Ok(new
            {
                stage = "IDI-P3",
                controlWriteAllowed = false,
                productionAutomationAllowed = false,
                proposal,
                approvals,
                outcome
            });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpPost("{proposalId}/approve")]
    public Task<IActionResult> Approve(string proposalId, [FromBody] DecisionGovernanceActionRequest request, CancellationToken ct) =>
        Transition(proposalId, DecisionProposalStatus.Approved, request, ct);

    [HttpPost("{proposalId}/reject")]
    public Task<IActionResult> Reject(string proposalId, [FromBody] DecisionGovernanceActionRequest request, CancellationToken ct) =>
        Transition(proposalId, DecisionProposalStatus.Rejected, request, ct);

    [HttpPost("{proposalId}/outcome")]
    public async Task<IActionResult> Outcome(string proposalId, [FromBody] DecisionOutcomeRequest request, CancellationToken ct)
    {
        if (!TryAllowP3(out _)) return NotFound();
        if (!ValidId(proposalId, 64) || request is null) return BadRequest("proposalId/request is invalid.");
        if (!ValidText(request.OutcomeType, 120) || !ValidText(request.ActualReference, 500))
            return BadRequest("outcomeType and actualReference are required and bounded.");
        if (request.ObservedAtUtc == default) return BadRequest("observedAtUtc is required.");
        if (!ValidHash(request.EvidenceHash)) return BadRequest("evidenceHash must be SHA-256 hex.");
        try
        {
            var store = GetFactory().CreateStore();
            var proposal = await store.GetAsync(proposalId.Trim(), ct);
            if (proposal is null) return NotFound();
            if (proposal.Status != DecisionProposalStatus.Approved)
                return Conflict(new { failClosed = true, error = "Outcome can only be recorded for an approved proposal." });

            var outcome = new DecisionOutcome(
                proposal.ProposalId,
                request.OutcomeType.Trim(),
                request.ActualReference.Trim(),
                request.ActualBenefit,
                request.ObservedAtUtc,
                request.EvidenceHash.Trim().ToLowerInvariant());
            var value = await store.RecordOutcomeAsync(outcome, ct);
            return Ok(new { stage = "IDI-P3", controlWriteAllowed = false, value });
        }
        catch (InvalidOperationException ex) { return Conflict(new { failClosed = true, error = ex.Message }); }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    private async Task<IActionResult> Transition(
        string proposalId,
        DecisionProposalStatus target,
        DecisionGovernanceActionRequest request,
        CancellationToken ct)
    {
        if (!TryAllowP3(out _)) return NotFound();
        if (!ValidId(proposalId, 64) || request is null) return BadRequest("proposalId/request is invalid.");
        if (!ValidText(request.Actor, 200) || !ValidText(request.Reason, 2000) ||
            !ValidText(request.CorrelationId, 120) || !ValidText(request.IdempotencyKey, 160))
            return BadRequest("actor, reason, correlationId and idempotencyKey are required and bounded.");
        try
        {
            var store = GetFactory().CreateStore();
            var proposal = await store.GetAsync(proposalId.Trim(), ct);
            if (proposal is null) return NotFound();
            var now = DateTimeOffset.UtcNow;
            if (proposal.IsExpired(now))
                return Conflict(new { failClosed = true, error = "Expired proposal cannot transition." });
            if (proposal.Status is DecisionProposalStatus.Blocked or DecisionProposalStatus.Approved or
                DecisionProposalStatus.Rejected or DecisionProposalStatus.OutcomeRecorded or DecisionProposalStatus.Expired)
                return Conflict(new { failClosed = true, error = $"Proposal in state {proposal.Status} cannot transition to {target}." });

            var hash = DecisionHash.Sha256(
                proposal.ProposalId,
                proposal.Status.ToString(),
                target.ToString(),
                request.Actor.Trim(),
                request.Reason.Trim(),
                now.ToString("O"),
                request.CorrelationId.Trim(),
                request.IdempotencyKey.Trim());
            var entry = new DecisionApprovalEntry(
                proposal.ProposalId,
                proposal.Status,
                target,
                request.Actor.Trim(),
                request.Reason.Trim(),
                now,
                request.CorrelationId.Trim(),
                request.IdempotencyKey.Trim(),
                hash);
            var value = await store.AppendApprovalAsync(entry, ct);
            return Ok(new
            {
                stage = "IDI-P3",
                governanceOnly = true,
                controlWriteAllowed = false,
                productionAutomationAllowed = false,
                value
            });
        }
        catch (InvalidOperationException ex) { return Conflict(new { failClosed = true, error = ex.Message }); }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    private DecisionIntelligencePersistenceFactory GetFactory()
    {
        var cs = _configuration.GetConnectionString("WcsDb")
                 ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new DecisionIntelligencePersistenceFactory(cs);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowP3(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Shadow Decision governance operation failed closed",
        detail: ex.Message);

    private static bool ValidId(string? value, int max) => ValidText(value, max);
    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
    private static bool ValidHash(string? value) =>
        value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));
}

public sealed class DecisionGovernanceActionRequest
{
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
}

public sealed class DecisionOutcomeRequest
{
    public string OutcomeType { get; init; } = string.Empty;
    public string ActualReference { get; init; } = string.Empty;
    public decimal? ActualBenefit { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public string EvidenceHash { get; init; } = string.Empty;
}
