namespace Wcs.Simulator.Tests;

using Wcs.DecisionIntelligence;
using Wcs.Infrastructure.IndustrialIntelligence;

public sealed class DecisionIntelligenceSqlPersistenceTests
{
    [Fact]
    public async Task SqlStore_AddGet_RoundTripsProposalAndEvidence()
    {
        var store = CreateStore();
        var proposal = CreateProposal(NewId("roundtrip"));

        await store.AddAsync(proposal, default);
        var loaded = await store.GetAsync(proposal.ProposalId, default);

        Assert.NotNull(loaded);
        Assert.Equal(proposal.ProposalId, loaded!.ProposalId);
        Assert.Equal(DecisionProposalStatus.PendingApproval, loaded.Status);
        Assert.Equal(proposal.Candidate.Action, loaded.Candidate.Action);
        Assert.Equal(proposal.Evidence.FeatureSnapshotId, loaded.Evidence.FeatureSnapshotId);
        Assert.Single(loaded.Constraints);
        Assert.False(loaded.ControlWriteAllowed);
    }

    [Fact]
    public async Task SqlStore_ProposalInsert_IsIdempotent_AndConflictsFailClosed()
    {
        var store = CreateStore();
        var proposal = CreateProposal(NewId("idempotent"));

        var first = await store.AddAsync(proposal, default);
        var replay = await store.AddAsync(proposal, default);

        Assert.Equal(first.ProposalId, replay.ProposalId);

        var conflicting = proposal with
        {
            Candidate = proposal.Candidate with { Action = "different-governance-recommendation" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(conflicting, default));
    }

    [Fact]
    public async Task SqlStore_Approval_AppendsAudit_AndUpdatesProposalStatusOnly()
    {
        var store = CreateStore();
        var proposal = CreateProposal(NewId("approval"));
        await store.AddAsync(proposal, default);

        var utc = DateTimeOffset.UtcNow;
        var idempotencyKey = NewId("approve-key");
        var entry = new DecisionApprovalEntry(
            proposal.ProposalId,
            DecisionProposalStatus.PendingApproval,
            DecisionProposalStatus.Approved,
            "sql-test",
            "approve recommendation for evaluation only",
            utc,
            NewId("correlation"),
            idempotencyKey,
            DecisionHash.Sha256(proposal.ProposalId, "approve", idempotencyKey));

        await store.AppendApprovalAsync(entry, default);
        var loaded = await store.GetAsync(proposal.ProposalId, default);
        var approvals = await store.GetApprovalsAsync(proposal.ProposalId, 20, default);

        Assert.NotNull(loaded);
        Assert.Equal(DecisionProposalStatus.Approved, loaded!.Status);
        Assert.False(loaded.ControlWriteAllowed);
        Assert.Contains(approvals, x => x.IdempotencyKey == idempotencyKey && x.ToStatus == DecisionProposalStatus.Approved);
    }

    [Fact]
    public async Task SqlStore_Outcome_RoundTrips_AndMarksOutcomeRecorded()
    {
        var store = CreateStore();
        var proposal = CreateProposal(NewId("outcome"));
        await store.AddAsync(proposal, default);
        await ApproveAsync(store, proposal);

        var outcome = new DecisionOutcome(
            proposal.ProposalId,
            "ObservedResult",
            "work-order/sql-test",
            12.5m,
            DateTimeOffset.UtcNow,
            DecisionHash.Sha256("outcome", proposal.ProposalId));

        await store.RecordOutcomeAsync(outcome, default);
        var loadedOutcome = await store.GetOutcomeAsync(proposal.ProposalId, default);
        var loadedProposal = await store.GetAsync(proposal.ProposalId, default);

        Assert.NotNull(loadedOutcome);
        Assert.Equal(outcome.ActualReference, loadedOutcome!.ActualReference);
        Assert.Equal(DecisionProposalStatus.OutcomeRecorded, loadedProposal!.Status);
        Assert.False(loadedProposal.ControlWriteAllowed);
    }

    [Fact]
    public async Task SqlStore_Recovery_RestoresProposalApprovalAndOutcome()
    {
        var store = CreateStore();
        var proposal = CreateProposal(NewId("recovery"));
        await store.AddAsync(proposal, default);
        await ApproveAsync(store, proposal);
        var outcome = new DecisionOutcome(
            proposal.ProposalId,
            "ObservedResult",
            "recovery-evidence",
            null,
            DateTimeOffset.UtcNow,
            DecisionHash.Sha256("recovery-outcome", proposal.ProposalId));
        await store.RecordOutcomeAsync(outcome, default);

        var recovery = await store.RecoverAsync(DateTimeOffset.UtcNow.AddMinutes(1), 1000, default);

        Assert.Contains(recovery.Proposals, x => x.ProposalId == proposal.ProposalId && x.Status == DecisionProposalStatus.OutcomeRecorded);
        Assert.Contains(recovery.ApprovalJournal, x => x.ProposalId == proposal.ProposalId && x.ToStatus == DecisionProposalStatus.Approved);
        Assert.Contains(recovery.Outcomes, x => x.ProposalId == proposal.ProposalId && x.ActualReference == "recovery-evidence");
    }

    private static async Task ApproveAsync(SqlDecisionProposalStore store, DecisionProposal proposal)
    {
        var key = NewId("approval");
        await store.AppendApprovalAsync(new DecisionApprovalEntry(
            proposal.ProposalId,
            DecisionProposalStatus.PendingApproval,
            DecisionProposalStatus.Approved,
            "sql-test",
            "governance-only approval",
            DateTimeOffset.UtcNow,
            NewId("correlation"),
            key,
            DecisionHash.Sha256(proposal.ProposalId, "approve", key)), default);
    }

    private static SqlDecisionProposalStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("WCS_P3_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("WCS_P3_SQL_CONNECTION is required for P3 SQL integration tests.");
        var factory = new DecisionIntelligencePersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory.CreateStore();
    }

    private static DecisionProposal CreateProposal(string proposalId)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = NewId("snapshot");
        return new DecisionProposal(
            proposalId,
            ProposalType.MaintenanceWindowRecommendation,
            DecisionProposalStatus.PendingApproval,
            now,
            now.AddHours(1),
            NewId("correlation"),
            NewId("proposal-key"),
            new DecisionCandidate(
                NewId("candidate"),
                "recommend-maintenance-window",
                0.91m,
                new Dictionary<string, string> { ["assetId"] = "asset-sql-01" }),
            new DecisionEvidence(
                "model-sql",
                "v1",
                snapshot,
                DecisionHash.Sha256("schema", proposalId),
                DecisionHash.Sha256("model-evidence", proposalId)),
            new DecisionExplanation(
                "SQL persistence acceptance recommendation.",
                ["health-degraded", "maintenance-window-available"],
                DecisionHash.Sha256("explanation", proposalId)),
            [new ConstraintResult("zero-control", true, "proposal does not write control", DecisionHash.Sha256("constraint", proposalId))]);
    }

    private static string NewId(string prefix) => $"p3-{prefix}-{Guid.NewGuid():N}";
}
