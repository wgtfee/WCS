namespace Wcs.DecisionIntelligence;

public sealed record DecisionActorReason(string Actor, string Reason)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Actor)) throw new ArgumentException("Actor is required.", nameof(Actor));
        if (string.IsNullOrWhiteSpace(Reason)) throw new ArgumentException("Reason is required.", nameof(Reason));
    }
}

public sealed record DecisionApprovalEntry(
    string ProposalId,
    DecisionProposalStatus FromStatus,
    DecisionProposalStatus ToStatus,
    string Actor,
    string Reason,
    DateTimeOffset Utc,
    string CorrelationId,
    string IdempotencyKey,
    string EntryHash);

public sealed record DecisionOutcome(
    string ProposalId,
    string OutcomeType,
    string ActualReference,
    decimal? ActualBenefit,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash);

public sealed class DecisionProposalJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DecisionProposal> _proposals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecisionApprovalEntry> _idempotency = new(StringComparer.Ordinal);
    private readonly List<DecisionApprovalEntry> _audit = [];
    private readonly Dictionary<string, DecisionOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly DecisionGovernanceLimits _limits;

    public DecisionProposalJournal(DecisionGovernanceLimits? limits = null)
    {
        _limits = limits ?? new DecisionGovernanceLimits();
        _limits.Validate();
    }

    public DecisionProposal Add(DecisionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            if (_proposals.TryGetValue(proposal.ProposalId, out var existing)) return existing;
            var pending = _proposals.Values.Count(x => x.Status is DecisionProposalStatus.Shadow or DecisionProposalStatus.PendingApproval);
            if (pending >= _limits.MaximumPendingProposals) throw new InvalidOperationException("Maximum pending proposal bound reached.");
            _proposals.Add(proposal.ProposalId, proposal);
            return proposal;
        }
    }

    public DecisionProposal? Get(string proposalId)
    {
        lock (_gate) return _proposals.GetValueOrDefault(proposalId);
    }

    public IReadOnlyList<DecisionProposal> List(int take = 100)
    {
        if (take is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(take));
        lock (_gate) return _proposals.Values.OrderByDescending(x => x.CreatedAtUtc).Take(take).ToArray();
    }

    public DecisionApprovalEntry Approve(string proposalId, DecisionActorReason actorReason, DateTimeOffset utc, string correlationId, string idempotencyKey)
        => Transition(proposalId, DecisionProposalStatus.Approved, actorReason, utc, correlationId, idempotencyKey);

    public DecisionApprovalEntry Reject(string proposalId, DecisionActorReason actorReason, DateTimeOffset utc, string correlationId, string idempotencyKey)
        => Transition(proposalId, DecisionProposalStatus.Rejected, actorReason, utc, correlationId, idempotencyKey);

    public DecisionOutcome RecordOutcome(DecisionOutcome outcome)
    {
        lock (_gate)
        {
            if (!_proposals.TryGetValue(outcome.ProposalId, out var proposal)) throw new KeyNotFoundException("Proposal not found.");
            DecisionGovernancePolicy.ValidateOutcome(outcome, proposal.CreatedAtUtc);
            if (_outcomes.TryGetValue(outcome.ProposalId, out var existing)) return existing;
            _outcomes.Add(outcome.ProposalId, outcome);
            _proposals[outcome.ProposalId] = proposal with { Status = DecisionProposalStatus.OutcomeRecorded };
            return outcome;
        }
    }

    public IReadOnlyList<DecisionApprovalEntry> Audit(string proposalId)
    {
        lock (_gate) return _audit.Where(x => x.ProposalId == proposalId).ToArray();
    }

    private DecisionApprovalEntry Transition(string proposalId, DecisionProposalStatus target, DecisionActorReason actorReason, DateTimeOffset utc, string correlationId, string idempotencyKey)
    {
        actorReason.Validate();
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("CorrelationId is required.", nameof(correlationId));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));
        lock (_gate)
        {
            if (_idempotency.TryGetValue(idempotencyKey, out var replay)) return replay;
            if (!_proposals.TryGetValue(proposalId, out var proposal)) throw new KeyNotFoundException("Proposal not found.");
            if (proposal.IsExpired(utc))
            {
                _proposals[proposalId] = proposal with { Status = DecisionProposalStatus.Expired };
                throw new InvalidOperationException("Expired proposal cannot be approved or rejected.");
            }
            if (proposal.Status is DecisionProposalStatus.Blocked or DecisionProposalStatus.Rejected or DecisionProposalStatus.Approved or DecisionProposalStatus.OutcomeRecorded)
                throw new InvalidOperationException($"Proposal in state {proposal.Status} cannot transition to {target}.");
            var hash = DecisionHash.Sha256(proposalId, proposal.Status.ToString(), target.ToString(), actorReason.Actor, actorReason.Reason, utc.ToUniversalTime().ToString("O"), correlationId, idempotencyKey);
            var entry = new DecisionApprovalEntry(proposalId, proposal.Status, target, actorReason.Actor.Trim(), actorReason.Reason.Trim(), utc, correlationId, idempotencyKey, hash);
            _proposals[proposalId] = proposal with { Status = target };
            _idempotency.Add(idempotencyKey, entry);
            _audit.Add(entry);
            return entry;
        }
    }
}
