namespace Wcs.Simulator.Tests;

using Wcs.IndustrialIntelligence.Governance;

public sealed class BoundedAutomationReadinessStressTests
{
    private const string GitSha40 = "dddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public void TenThousandIdenticalEvaluations_AreDeterministic()
    {
        var request = ValidRequest(AutomationLevel.L1, realEvidence: false);
        var first = BoundedAutomationReadinessEvaluator.Evaluate(request);
        var firstHash = BoundedAutomationReadinessEvidenceHash.Compute(request, first);
        for (var i = 0; i < 10_000; i++)
        {
            var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
            Assert.Equal(first, decision);
            Assert.Equal(firstHash, BoundedAutomationReadinessEvidenceHash.Compute(request, decision));
        }
    }

    [Fact]
    public void TwentyThousandParallelEvaluations_NeverGrantProduction()
    {
        var request = ValidRequest(AutomationLevel.L2, realEvidence: true);
        var violations = 0;
        Parallel.For(0, 20_000, _ =>
        {
            var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
            if (!decision.SoftwareSideReady || decision.ProductionEnablementAllowed || decision.Claim != "software-side ready only")
                Interlocked.Increment(ref violations);
        });
        Assert.Equal(0, violations);
    }

    [Fact]
    public async Task FiveThousandParallelEvidenceAppends_RemainImmutableAndQueryable()
    {
        var store = new InMemoryBoundedAutomationReadinessEvidenceStore();
        var request = ValidRequest(AutomationLevel.L1, realEvidence: false);
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        await Parallel.ForEachAsync(Enumerable.Range(0, 5_000), async (index, ct) =>
        {
            var record = BoundedAutomationReadinessEvidenceRecord.Create(
                $"stress-{index:D5}", DateTimeOffset.UnixEpoch.AddSeconds(index), request, decision);
            await store.AppendAsync(record, ct);
        });
        var latest = await store.ListAsync(500);
        Assert.Equal(500, latest.Count);
        Assert.Equal("stress-04999", latest[0].EvaluationId);
        Assert.NotNull(await store.GetAsync("stress-02500"));
    }

    [Fact]
    public void PermanentProhibitions_RemainDeniedAcrossRepeatedEvaluation()
    {
        foreach (var prohibited in Enum.GetValues<PermanentAutomationProhibition>())
        {
            var request = ValidRequest(AutomationLevel.L1, realEvidence: false) with
            {
                RequestedProhibitedOperations = new[] { prohibited }
            };
            for (var i = 0; i < 1_000; i++)
            {
                var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
                Assert.False(decision.SoftwareSideReady);
                Assert.False(decision.ProductionEnablementAllowed);
            }
        }
    }

    [Fact]
    public void L2WithoutRealEvidence_RemainsFailClosedAcrossFiveThousandRuns()
    {
        var request = ValidRequest(AutomationLevel.L2, realEvidence: false);
        for (var i = 0; i < 5_000; i++)
        {
            var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
            Assert.False(decision.SoftwareSideReady);
            Assert.False(decision.ProductionEnablementAllowed);
            Assert.Equal(AutomationLevel.L0, decision.EffectiveMaximumAutomationLevel);
        }
    }

    [Fact]
    public void L3WithAllEvidence_RemainsSoftwareOnlyAcrossFiveThousandRuns()
    {
        var request = ValidRequest(AutomationLevel.L3, realEvidence: true);
        for (var i = 0; i < 5_000; i++)
        {
            var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
            Assert.True(decision.SoftwareSideReady);
            Assert.False(decision.ProductionEnablementAllowed);
            Assert.Equal(AutomationLevel.L3, decision.EffectiveMaximumAutomationLevel);
            Assert.Equal("software-side ready only", decision.Claim);
        }
    }

    private static BoundedAutomationReadinessRequest ValidRequest(AutomationLevel level, bool realEvidence) => new(
        "IndustrialIntelligence",
        new AutomationPolicy(true, level, "stress-v1", Hashing.Sha256($"stress-policy:{level}")),
        new ExecutionAllowance(true, ExecutionAllowanceKind.SoftwareSimulation),
        new RateLimit(true, 120),
        new BudgetLimit(true, 500m),
        new MaintenanceWindow(true, TimeSpan.FromHours(2), TimeSpan.FromHours(4)),
        new ApprovalRequirement(true, 2, true),
        new CircuitBreaker(true, 3, TimeSpan.FromMinutes(1)),
        new KillSwitch(true, true),
        new RollbackPolicy(true, "stress-v0", TimeSpan.FromMinutes(5)),
        new BoundedAutomationEvidence(true, realEvidence, realEvidence, realEvidence, realEvidence, GitSha40, Hashing.Sha256("stress-evidence")),
        Array.Empty<PermanentAutomationProhibition>());
}
