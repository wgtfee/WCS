namespace Wcs.DecisionIntelligence;

public sealed record DecisionQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    ProposalType? Type = null,
    DecisionProposalStatus? Status = null,
    int Take = 100)
{
    public void Validate()
    {
        if (Take is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(Take));
        if (FromUtc is not null && ToUtc is not null && FromUtc > ToUtc)
            throw new ArgumentException("FromUtc must not be later than ToUtc.");
        if (FromUtc is not null && ToUtc is not null && ToUtc.Value - FromUtc.Value > TimeSpan.FromDays(180))
            throw new ArgumentOutOfRangeException(nameof(ToUtc), "Decision query range exceeds governed 180 day bound.");
    }
}

public interface IDecisionProposalStore
{
    Task<DecisionProposal> AddAsync(DecisionProposal proposal, CancellationToken ct);
    Task<DecisionProposal?> GetAsync(string proposalId, CancellationToken ct);
    Task<IReadOnlyList<DecisionProposal>> QueryAsync(DecisionQuery query, CancellationToken ct);
    Task<DecisionApprovalEntry> AppendApprovalAsync(DecisionApprovalEntry entry, CancellationToken ct);
    Task<DecisionOutcome> RecordOutcomeAsync(DecisionOutcome outcome, CancellationToken ct);
}

public sealed record DecisionPersistenceRecovery(
    IReadOnlyList<DecisionProposal> Proposals,
    IReadOnlyList<DecisionApprovalEntry> ApprovalJournal,
    IReadOnlyList<DecisionOutcome> Outcomes);

public interface IDecisionRecoveryStore
{
    Task<DecisionPersistenceRecovery> RecoverAsync(DateTimeOffset asOfUtc, int maximumProposals, CancellationToken ct);
}

/// <summary>
/// Boundary marker for P3 persistence adapters. Implementations may use SQL/MES outside this project,
/// but callers must execute them off the deterministic PLC/task/dispatch control path.
/// </summary>
public interface IDecisionPersistenceHealth
{
    bool IsAvailable { get; }
    string? LastFailure { get; }
}
