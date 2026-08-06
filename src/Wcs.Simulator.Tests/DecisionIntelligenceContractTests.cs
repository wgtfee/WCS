using Wcs.DecisionIntelligence;

namespace Wcs.Simulator.Tests;

public sealed class DecisionIntelligenceContractTests
{
    private static DecisionContext Context(string? snapshot="snap-1", string? model="health", string? version="1.0.0") =>
        new("corr-1", "idem-1", ProposalType.MaintenanceWindowRecommendation, snapshot, model, version,
            new string('a',64), DateTimeOffset.Parse("2026-08-05T00:00:00Z"), DateTimeOffset.Parse("2026-08-06T00:00:00Z"));

    private static ShadowDecisionProposalEngine Engine(params IDecisionConstraintEvaluator[] constraints) =>
        new(constraints, (ctx, ct) => Task.FromResult<DecisionCandidate?>(new("candidate-1","recommend-only",0.9m,new Dictionary<string,string>())));

    [Fact] public async Task Missing_snapshot_fails_closed() => Assert.Equal("FeatureSnapshotRequired", (await Engine().EvaluateAsync(Context(snapshot:null), default)).Code);
    [Fact] public async Task Missing_champion_fails_closed() => Assert.Equal("ModelUnavailable", (await Engine().EvaluateAsync(Context(model:null), default)).Code);
    [Fact] public async Task Missing_champion_version_fails_closed() => Assert.Equal("ModelUnavailable", (await Engine().EvaluateAsync(Context(version:null), default)).Code);
    [Fact] public async Task Valid_context_generates_shadow_only() { var r=await Engine().EvaluateAsync(Context(),default); Assert.True(r.Generated); Assert.Equal(DecisionProposalStatus.Shadow,r.Proposal!.Status); Assert.False(r.Proposal.ControlWriteAllowed); }
    [Fact] public async Task Proposal_id_is_deterministic() { var a=await Engine().EvaluateAsync(Context(),default); var b=await Engine().EvaluateAsync(Context(),default); Assert.Equal(a.Proposal!.ProposalId,b.Proposal!.ProposalId); }
    [Fact] public async Task Evidence_hash_is_sha256() { var r=await Engine().EvaluateAsync(Context(),default); Assert.Equal(64,r.Proposal!.Evidence.EvidenceHash.Length); }
    [Fact] public async Task Hard_constraint_blocks_candidate() { var r=await Engine(new FixedConstraint(false)).EvaluateAsync(Context(),default); Assert.Equal(DecisionProposalStatus.Blocked,r.Proposal!.Status); Assert.Equal("Blocked",r.Code); }
    [Fact] public async Task Passing_constraint_keeps_shadow() { var r=await Engine(new FixedConstraint(true)).EvaluateAsync(Context(),default); Assert.Equal(DecisionProposalStatus.Shadow,r.Proposal!.Status); }
    [Fact] public void Context_requires_correlation() { var c=Context() with { CorrelationId="" }; Assert.Throws<ArgumentException>(()=>DecisionGovernancePolicy.ValidateContext(c)); }
    [Fact] public void Context_requires_idempotency_key() { var c=Context() with { IdempotencyKey="" }; Assert.Throws<ArgumentException>(()=>DecisionGovernancePolicy.ValidateContext(c)); }
    [Fact] public void Context_requires_schema_hash() { var c=Context() with { FeatureSchemaHash="" }; Assert.Throws<ArgumentException>(()=>DecisionGovernancePolicy.ValidateContext(c)); }
    [Fact] public void Expiry_must_follow_asof() { var c=Context() with { ExpiresAtUtc=Context().AsOfUtc }; Assert.Throws<ArgumentException>(()=>DecisionGovernancePolicy.ValidateContext(c)); }
    [Fact] public async Task Approval_is_audited_and_idempotent() { var p=(await Engine().EvaluateAsync(Context(),default)).Proposal!; var j=new DecisionProposalJournal(); j.Add(p); var t=DateTimeOffset.Parse("2026-08-05T01:00:00Z"); var a=j.Approve(p.ProposalId,new("operator","reviewed"),t,"corr","approve-1"); var b=j.Approve(p.ProposalId,new("operator","reviewed"),t,"corr","approve-1"); Assert.Equal(a.EntryHash,b.EntryHash); Assert.Single(j.Audit(p.ProposalId)); }
    [Fact] public async Task Rejection_is_audited() { var p=(await Engine().EvaluateAsync(Context(),default)).Proposal!; var j=new DecisionProposalJournal(); j.Add(p); j.Reject(p.ProposalId,new("operator","unsafe"),DateTimeOffset.Parse("2026-08-05T01:00:00Z"),"corr","reject-1"); Assert.Equal(DecisionProposalStatus.Rejected,j.Get(p.ProposalId)!.Status); }
    [Fact] public async Task Expired_proposal_cannot_be_approved() { var p=(await Engine().EvaluateAsync(Context(),default)).Proposal!; var j=new DecisionProposalJournal(); j.Add(p); Assert.Throws<InvalidOperationException>(()=>j.Approve(p.ProposalId,new("operator","late"),p.ExpiresAtUtc,"corr","late-1")); Assert.Equal(DecisionProposalStatus.Expired,j.Get(p.ProposalId)!.Status); }
    [Fact] public async Task Outcome_cannot_predate_proposal() { var p=(await Engine().EvaluateAsync(Context(),default)).Proposal!; var j=new DecisionProposalJournal(); j.Add(p); Assert.Throws<ArgumentException>(()=>j.RecordOutcome(new(p.ProposalId,"done","task-1",1m,p.CreatedAtUtc.AddSeconds(-1),new string('b',64)))); }
    [Fact] public async Task Outcome_is_idempotent_per_proposal() { var p=(await Engine().EvaluateAsync(Context(),default)).Proposal!; var j=new DecisionProposalJournal(); j.Add(p); var o=new DecisionOutcome(p.ProposalId,"done","task-1",1m,p.CreatedAtUtc.AddHours(1),new string('b',64)); Assert.Equal(j.RecordOutcome(o),j.RecordOutcome(o)); }
    [Fact] public void Actor_and_reason_are_required() { Assert.Throws<ArgumentException>(()=>new DecisionActorReason("","reason").Validate()); Assert.Throws<ArgumentException>(()=>new DecisionActorReason("actor","").Validate()); }
    [Fact] public void Query_is_bounded() { Assert.Throws<ArgumentOutOfRangeException>(()=>new DecisionQuery(Take:1001).Validate()); }
    [Fact] public void Query_range_is_bounded() { Assert.Throws<ArgumentOutOfRangeException>(()=>new DecisionQuery(DateTimeOffset.UtcNow.AddDays(-181),DateTimeOffset.UtcNow).Validate()); }
    [Fact] public void Governance_limits_are_bounded() { Assert.Throws<ArgumentOutOfRangeException>(()=>new DecisionGovernanceLimits(MaximumPendingProposals:100001).Validate()); }
    [Fact] public void Proposal_types_are_exact_first_wave() => Assert.Equal(6,Enum.GetValues<ProposalType>().Length);
    [Fact] public void Metrics_start_at_zero() { var s=new DecisionMetrics().Snapshot(); Assert.Equal(0,s.ProposalGeneratedTotal); Assert.Equal(0,s.ProposalActualBenefit); }
    [Fact] public void Decision_hash_is_deterministic() => Assert.Equal(DecisionHash.Sha256("a","b"),DecisionHash.Sha256("a","b"));

    private sealed class FixedConstraint(bool passed) : IDecisionConstraintEvaluator
    {
        public Task<IReadOnlyList<ConstraintResult>> EvaluateAsync(DecisionContext context, DecisionCandidate candidate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConstraintResult>>([new("hard.safety",passed,passed?"ok":"blocked",new string('c',64))]);
    }
}
