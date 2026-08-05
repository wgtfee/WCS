namespace Wcs.DecisionIntelligence;

public sealed class ShadowDecisionProposalEngine : IDecisionProposalEngine
{
    private readonly IReadOnlyList<IDecisionConstraintEvaluator> _constraints;
    private readonly Func<DecisionContext, CancellationToken, Task<DecisionCandidate?>> _candidateFactory;

    public ShadowDecisionProposalEngine(
        IEnumerable<IDecisionConstraintEvaluator> constraints,
        Func<DecisionContext, CancellationToken, Task<DecisionCandidate?>> candidateFactory)
    {
        _constraints = constraints.ToArray();
        _candidateFactory = candidateFactory;
    }

    public async Task<DecisionProposalResult> EvaluateAsync(DecisionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.FeatureSnapshotId)) return DecisionProposalResult.MissingSnapshot();
        if (string.IsNullOrWhiteSpace(context.ChampionModelId) || string.IsNullOrWhiteSpace(context.ChampionModelVersion))
            return DecisionProposalResult.ModelUnavailable();
        if (context.ExpiresAtUtc <= context.AsOfUtc) return new(false, "InvalidExpiry", null);

        var candidate = await _candidateFactory(context, ct).ConfigureAwait(false);
        if (candidate is null) return new(false, "NoCandidate", null);

        var results = new List<ConstraintResult>();
        foreach (var evaluator in _constraints)
            results.AddRange(await evaluator.EvaluateAsync(context, candidate, ct).ConfigureAwait(false));

        var blocked = results.Any(x => !x.Passed);
        var evidenceHash = DecisionHash.Sha256(context.ChampionModelId!, context.ChampionModelVersion!, context.FeatureSnapshotId!, context.FeatureSchemaHash);
        var explanationHash = DecisionHash.Sha256(candidate.CandidateId, candidate.Action, string.Join("|", results.Select(x => $"{x.Code}:{x.Passed}:{x.EvidenceHash}")));
        var proposalId = DecisionHash.Sha256(context.IdempotencyKey, context.Type.ToString(), evidenceHash)[..32];
        var proposal = new DecisionProposal(
            proposalId, context.Type, blocked ? DecisionProposalStatus.Blocked : DecisionProposalStatus.Shadow,
            context.AsOfUtc, context.ExpiresAtUtc, context.CorrelationId, context.IdempotencyKey, candidate,
            new DecisionEvidence(context.ChampionModelId!, context.ChampionModelVersion!, context.FeatureSnapshotId!, context.FeatureSchemaHash, evidenceHash),
            new DecisionExplanation(blocked ? "Candidate blocked by hard constraints." : "Shadow recommendation only; no control write is permitted.",
                results.Select(x => x.Code).ToArray(), explanationHash), results);
        return new(true, blocked ? "Blocked" : "Shadow", proposal);
    }
}
