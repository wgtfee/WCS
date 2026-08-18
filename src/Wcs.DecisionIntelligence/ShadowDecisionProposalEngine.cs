namespace Wcs.DecisionIntelligence;

public sealed class ShadowDecisionProposalEngine : IDecisionProposalEngine
{
    private readonly IReadOnlyList<IDecisionConstraintEvaluator> _constraints;
    private readonly Func<DecisionContext, CancellationToken, Task<DecisionCandidate?>> _candidateFactory;
    private readonly DecisionGovernanceLimits _limits;

    public ShadowDecisionProposalEngine(
        IEnumerable<IDecisionConstraintEvaluator> constraints,
        Func<DecisionContext, CancellationToken, Task<DecisionCandidate?>> candidateFactory,
        DecisionGovernanceLimits? limits = null)
    {
        _constraints = constraints.ToArray();
        _candidateFactory = candidateFactory;
        _limits = limits ?? new DecisionGovernanceLimits();
        _limits.Validate();
        if (_constraints.Count > _limits.MaximumConstraintsPerProposal)
            throw new ArgumentOutOfRangeException(nameof(constraints), "Constraint evaluator count exceeds governed bound.");
    }

    public async Task<DecisionProposalResult> EvaluateAsync(DecisionContext context, CancellationToken ct)
    {
        DecisionGovernancePolicy.ValidateContext(context);
        if (string.IsNullOrWhiteSpace(context.FeatureSnapshotId)) return DecisionProposalResult.MissingSnapshot();
        if (string.IsNullOrWhiteSpace(context.ChampionModelId) || string.IsNullOrWhiteSpace(context.ChampionModelVersion))
            return DecisionProposalResult.ModelUnavailable();

        var candidate = await _candidateFactory(context, ct).ConfigureAwait(false);
        if (candidate is null) return new(false, "NoCandidate", null);

        var results = new List<ConstraintResult>();
        foreach (var evaluator in _constraints)
        {
            var evaluated = await evaluator.EvaluateAsync(context, candidate, ct).ConfigureAwait(false);
            if (results.Count + evaluated.Count > _limits.MaximumConstraintsPerProposal)
                return new(false, "ConstraintLimitExceeded", null);
            results.AddRange(evaluated);
        }

        var blocked = results.Any(x => !x.Passed);
        var evidenceHash = DecisionHash.Sha256(context.ChampionModelId!, context.ChampionModelVersion!, context.FeatureSnapshotId!, context.FeatureSchemaHash);
        var explanationHash = DecisionHash.Sha256(candidate.CandidateId, candidate.Action, string.Join("|", results.Select(x => $"{x.Code}:{x.Passed}:{x.EvidenceHash}")));
        var proposalId = DecisionHash.Sha256(context.IdempotencyKey, context.Type.ToString(), evidenceHash)[..32];
        var factors = results.Select(x => x.Code).Take(_limits.MaximumExplanationFactors).ToArray();
        var proposal = new DecisionProposal(
            proposalId, context.Type, blocked ? DecisionProposalStatus.Blocked : DecisionProposalStatus.Shadow,
            context.AsOfUtc, context.ExpiresAtUtc, context.CorrelationId, context.IdempotencyKey, candidate,
            new DecisionEvidence(context.ChampionModelId!, context.ChampionModelVersion!, context.FeatureSnapshotId!, context.FeatureSchemaHash, evidenceHash),
            new DecisionExplanation(blocked ? "Candidate blocked by hard constraints." : "Shadow recommendation only; no control write is permitted.",
                factors, explanationHash), results);
        return new(true, blocked ? "Blocked" : "Shadow", proposal);
    }
}
