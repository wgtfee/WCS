namespace Wcs.DecisionIntelligence;

public sealed record DecisionGovernanceLimits(
    int MaximumPendingProposals = 10_000,
    int MaximumConstraintsPerProposal = 128,
    int MaximumExplanationFactors = 128)
{
    public void Validate()
    {
        if (MaximumPendingProposals is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumPendingProposals));
        if (MaximumConstraintsPerProposal is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaximumConstraintsPerProposal));
        if (MaximumExplanationFactors is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaximumExplanationFactors));
    }
}

public static class DecisionGovernancePolicy
{
    public static void ValidateContext(DecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.CorrelationId)) throw new ArgumentException("CorrelationId is required.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey)) throw new ArgumentException("IdempotencyKey is required.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.FeatureSchemaHash)) throw new ArgumentException("FeatureSchemaHash is required.", nameof(context));
        if (context.ExpiresAtUtc <= context.AsOfUtc) throw new ArgumentException("ExpiresAtUtc must be after AsOfUtc.", nameof(context));
    }

    public static void ValidateOutcome(DecisionOutcome outcome, DateTimeOffset proposalCreatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (string.IsNullOrWhiteSpace(outcome.ProposalId)) throw new ArgumentException("ProposalId is required.", nameof(outcome));
        if (string.IsNullOrWhiteSpace(outcome.OutcomeType)) throw new ArgumentException("OutcomeType is required.", nameof(outcome));
        if (string.IsNullOrWhiteSpace(outcome.ActualReference)) throw new ArgumentException("ActualReference is required.", nameof(outcome));
        if (string.IsNullOrWhiteSpace(outcome.EvidenceHash) || outcome.EvidenceHash.Length != 64) throw new ArgumentException("EvidenceHash must be SHA-256 hex.", nameof(outcome));
        if (outcome.ObservedAtUtc < proposalCreatedAtUtc) throw new ArgumentException("Outcome cannot predate proposal.", nameof(outcome));
    }
}
