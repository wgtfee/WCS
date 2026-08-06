using Wcs.Optimization;
using Xunit;

namespace Wcs.Simulator.Tests;

public sealed class DigitalTwinOptimizerContractTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Constraint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Governance_is_recommendation_only()
    {
        Assert.Equal("L1", OptimizationGovernance.MaximumAutomationLevel);
        Assert.False(OptimizationGovernance.ControlWriteAllowed);
        Assert.False(OptimizationGovernance.AutoProductionPolicyReplacementAllowed);
    }

    [Fact]
    public void Definition_requires_at_least_three_candidates()
    {
        var definition = Definition(Policies().Take(2).ToArray());
        Assert.Throws<InvalidOperationException>(() => DigitalTwinOptimizer.ValidateDefinition(definition));
    }

    [Fact]
    public void Definition_requires_exactly_one_production_baseline()
    {
        var policies = Policies().Select(p => p with { Kind = OptimizationPolicyKind.ShortestDistance }).ToArray();
        Assert.Throws<InvalidOperationException>(() => DigitalTwinOptimizer.ValidateDefinition(Definition(policies)));
    }

    [Fact]
    public void Definition_rejects_constraint_profile_drift()
    {
        var policies = Policies().ToArray();
        policies[1] = policies[1] with { ConstraintProfileHash = new string('b', 64) };
        Assert.Throws<InvalidOperationException>(() => DigitalTwinOptimizer.ValidateDefinition(Definition(policies)));
    }

    [Fact]
    public void Definition_rejects_non_exact_software_head()
    {
        Assert.Throws<InvalidOperationException>(() => DigitalTwinOptimizer.ValidateDefinition(Definition(Policies()) with { SoftwareHead = "develop" }));
    }

    [Fact]
    public void Policy_hash_is_deterministic()
    {
        var policy = Policies().First();
        Assert.Equal(policy.PolicyHash, policy.PolicyHash);
        Assert.True(OptimizationHash.IsSha256(policy.PolicyHash));
    }

    [Fact]
    public void Experiment_hash_is_order_independent_for_candidates_and_seeds()
    {
        var first = Definition(Policies()) with { SeedSet = new[] { 7, 11 } };
        var second = first with { PolicyCandidates = first.PolicyCandidates.Reverse().ToArray(), SeedSet = new[] { 11, 7 } };
        Assert.Equal(first.DefinitionHash, second.DefinitionHash);
    }

    [Fact]
    public async Task Optimizer_runs_required_load_cases_and_two_determinism_rounds()
    {
        var definition = Definition(Policies());
        var runner = new DeterministicRunner();
        var result = await new DigitalTwinOptimizer(runner).EvaluateAsync(definition);
        var expectedPerPolicy = OptimizationGovernance.RequiredLoadCases.Length + 1;
        Assert.Equal(definition.PolicyCandidates.Count * definition.SeedSet.Count * expectedPerPolicy, result.Runs.Count);
        Assert.False(result.ControlWriteAllowed);
        Assert.False(result.AutoProductionPolicyReplacementAllowed);
        Assert.True(OptimizationHash.IsSha256(result.EvidenceHash));
    }

    [Fact]
    public async Task Optimizer_rejects_determinism_mismatch()
    {
        var runner = new DeterministicRunner(mismatchSecondRound: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DigitalTwinOptimizer(runner).EvaluateAsync(Definition(Policies())));
    }

    [Fact]
    public async Task Ranking_keeps_hard_constraint_failures_out_of_successful_runs()
    {
        var result = await new DigitalTwinOptimizer(new DeterministicRunner(failPolicy: "health")).EvaluateAsync(Definition(Policies()));
        var health = Assert.Single(result.Ranking.Where(x => x.PolicyId == "health"));
        Assert.Equal(0, health.SuccessfulRuns);
        Assert.True(health.FailedRuns > 0);
        Assert.Equal(double.NegativeInfinity, health.Score);
    }

    [Fact]
    public async Task Ranking_contains_pareto_evidence_for_valid_candidates()
    {
        var result = await new DigitalTwinOptimizer(new DeterministicRunner()).EvaluateAsync(Definition(Policies()));
        Assert.Equal(3, result.Ranking.Count);
        Assert.Contains(result.Ranking, x => x.ParetoEfficient);
    }

    [Fact]
    public void Required_load_cases_cover_stress_recovery_and_health()
    {
        Assert.Contains(OptimizationLoadCase.PeakLoad, OptimizationGovernance.RequiredLoadCases);
        Assert.Contains(OptimizationLoadCase.SegmentBlocked, OptimizationGovernance.RequiredLoadCases);
        Assert.Contains(OptimizationLoadCase.ExternalDependencyFailure, OptimizationGovernance.RequiredLoadCases);
        Assert.Contains(OptimizationLoadCase.HealthDegraded, OptimizationGovernance.RequiredLoadCases);
        Assert.Contains(OptimizationLoadCase.RestartRecovery, OptimizationGovernance.RequiredLoadCases);
        Assert.Contains(OptimizationLoadCase.DeterminismReplay, OptimizationGovernance.RequiredLoadCases);
    }

    private static OptimizationExperimentDefinition Definition(IEnumerable<OptimizationPolicyCandidate> policies) => new()
    {
        ExperimentId = "exp-p5-001",
        ScenarioSetVersion = "s0-s10-v1",
        SeedSet = new[] { 7 },
        TopologyRevision = "topology-r1",
        OrderDatasetVersion = "orders-r1",
        PolicyCandidates = policies.ToArray(),
        ObjectiveWeights = new OptimizationObjectiveWeights(),
        ConstraintProfileHash = Constraint,
        SoftwareHead = Head
    };

    private static IReadOnlyList<OptimizationPolicyCandidate> Policies() => new[]
    {
        Policy("baseline", OptimizationPolicyKind.CurrentProductionBaseline),
        Policy("shortest", OptimizationPolicyKind.ShortestDistance),
        Policy("health", OptimizationPolicyKind.HealthAware)
    };

    private static OptimizationPolicyCandidate Policy(string id, OptimizationPolicyKind kind) => new()
    {
        PolicyId = id, Version = "1.0.0", Kind = kind, ConstraintProfileHash = Constraint,
        ApprovedBy = "p5-test", ApprovedAtUtc = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class DeterministicRunner(bool mismatchSecondRound = false, string? failPolicy = null) : IDigitalTwinExperimentRunner
    {
        public Task<OptimizationScenarioRun> RunAsync(OptimizationExperimentDefinition definition, OptimizationPolicyCandidate policy,
            OptimizationLoadCase loadCase, int seed, int determinismRound, CancellationToken cancellationToken)
        {
            var suffix = mismatchSecondRound && loadCase == OptimizationLoadCase.DeterminismReplay && determinismRound == 2 ? "mismatch" : "stable";
            var finalHash = Hash($"final:{policy.PolicyId}:{loadCase}:{seed}:{suffix}");
            var metrics = new OptimizationMetrics
            {
                Throughput = policy.Kind == OptimizationPolicyKind.CurrentProductionBaseline ? 100 : 105,
                MeanMissionLeadTimeSeconds = 10, P95MissionLeadTimeSeconds = 12,
                MeanWaitTimeSeconds = 2, P95WaitTimeSeconds = 3, EnergyEstimate = 10,
                WearIndex = policy.Kind == OptimizationPolicyKind.HealthAware ? 5 : 6,
                FailureRiskExposure = policy.Kind == OptimizationPolicyKind.HealthAware ? 3 : 4,
                RecoveryTimeSeconds = 5
            };
            return Task.FromResult(new OptimizationScenarioRun
            {
                ExperimentId = definition.ExperimentId, PolicyId = policy.PolicyId, PolicyHash = policy.PolicyHash,
                LoadCase = loadCase, Seed = seed, DeterminismRound = determinismRound,
                ScenarioHash = Hash($"scenario:{loadCase}:{seed}"), SoftwareHead = definition.SoftwareHead,
                Metrics = metrics, FinalStateHash = finalHash, EvidenceHash = Hash($"evidence:{policy.PolicyId}:{loadCase}:{seed}:{determinismRound}"),
                HardConstraintsSatisfied = policy.PolicyId != failPolicy,
                FailureReason = policy.PolicyId == failPolicy ? "hard-constraint" : null
            });
        }

        private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}