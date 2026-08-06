namespace Wcs.Simulator.Tests;

using Wcs.Optimization;
using Wcs.Simulator.Optimization;

public sealed class DigitalTwinOptimizerIntegrationTests
{
    private const string Head = "2222222222222222222222222222222222222222";
    private const string Constraint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTime Approval = new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Standard_catalog_contains_all_six_governed_candidates()
    {
        var candidates = OptimizationPolicyCatalog.CreateStandardCandidates(Constraint, "p5-ci", Approval);

        Assert.Equal(6, candidates.Count);
        Assert.Equal(6, candidates.Select(static item => item.Kind).Distinct().Count());
        Assert.Single(candidates.Where(static item => item.Kind == OptimizationPolicyKind.CurrentProductionBaseline));
        Assert.All(candidates, static item => Assert.True(OptimizationHash.IsSha256(item.PolicyHash)));
        Assert.All(candidates, item => Assert.Equal(Constraint, item.ConstraintProfileHash));
    }

    [Fact]
    public async Task Governed_runner_emits_exact_s0_to_s10_software_evidence()
    {
        var definition = Definition(ThreeCandidates());
        var run = await new GovernedDigitalTwinExperimentRunner().RunAsync(
            definition, definition.PolicyCandidates[0], OptimizationLoadCase.NormalLoad, 7, 1, CancellationToken.None);

        Assert.True(run.HardConstraintsSatisfied);
        Assert.Equal(11, run.StageEvidence.Count);
        Assert.Equal(Enumerable.Range(0, 11), run.StageEvidence.Select(static item => (int)item.Stage));
        Assert.All(run.StageEvidence.Where(static item => item.Stage is >= OptimizationSimulationStage.S1ScenarioEngine and <= OptimizationSimulationStage.S8CapacityReadiness),
            static item => Assert.True(item.Executed));
        var s9 = Assert.Single(run.StageEvidence.Where(static item => item.Stage == OptimizationSimulationStage.S9HilSoftwareBoundary));
        Assert.True(s9.ReadOnlyBoundary);
        Assert.True(s9.RequiresRealHardware);
        Assert.False(s9.Executed);
        Assert.False(s9.RealHardwareExecuted);
        Assert.True(OptimizationHash.IsSha256(run.EvidenceHash));
    }

    [Fact]
    public async Task Governed_runner_same_seed_same_input_is_deterministic_across_two_rounds()
    {
        var definition = Definition(ThreeCandidates());
        var runner = new GovernedDigitalTwinExperimentRunner();
        var first = await runner.RunAsync(definition, definition.PolicyCandidates[1], OptimizationLoadCase.PeakLoad, 7, 1, CancellationToken.None);
        var second = await runner.RunAsync(definition, definition.PolicyCandidates[1], OptimizationLoadCase.PeakLoad, 7, 2, CancellationToken.None);

        Assert.Equal(first.ScenarioHash, second.ScenarioHash);
        Assert.Equal(first.FinalStateHash, second.FinalStateHash);
        Assert.Equal(first.Metrics, second.Metrics);
        Assert.Equal(first.HardConstraintsSatisfied, second.HardConstraintsSatisfied);
        Assert.NotEqual(first.EvidenceHash, second.EvidenceHash);
    }

    [Fact]
    public async Task Governed_runner_keeps_same_scenario_hash_for_different_policy_candidates()
    {
        var definition = Definition(ThreeCandidates());
        var runner = new GovernedDigitalTwinExperimentRunner();
        var baseline = await runner.RunAsync(definition, definition.PolicyCandidates[0], OptimizationLoadCase.HealthDegraded, 7, 1, CancellationToken.None);
        var candidate = await runner.RunAsync(definition, definition.PolicyCandidates[2], OptimizationLoadCase.HealthDegraded, 7, 1, CancellationToken.None);

        Assert.Equal(baseline.ScenarioHash, candidate.ScenarioHash);
        Assert.NotEqual(baseline.PolicyHash, candidate.PolicyHash);
        Assert.NotEqual(baseline.Metrics, candidate.Metrics);
    }

    [Fact]
    public async Task Optimizer_with_real_simulation_runner_compares_three_candidates_on_identical_inputs()
    {
        var definition = Definition(ThreeCandidates());
        var result = await new DigitalTwinOptimizer(new GovernedDigitalTwinExperimentRunner()).EvaluateAsync(definition);

        Assert.Equal(3, result.Ranking.Count);
        Assert.Equal(3 * 8 * 2, result.Runs.Count);
        Assert.All(result.Ranking, static item => Assert.True(item.HardConstraintQualified));
        Assert.Contains(result.Ranking, static item => item.ParetoEfficient);
        Assert.All(result.Runs, run => Assert.Equal(definition.ScenarioSetVersion, "s0-s10-v1"));
        Assert.False(result.ControlWriteAllowed);
        Assert.False(result.AutoProductionPolicyReplacementAllowed);
        Assert.False(result.ProductionAutomationAllowed);
    }

    [Fact]
    public async Task Six_policy_catalog_can_be_evaluated_without_production_policy_replacement()
    {
        var candidates = OptimizationPolicyCatalog.CreateStandardCandidates(Constraint, "p5-ci", Approval);
        var definition = Definition(candidates);
        var result = await new DigitalTwinOptimizer(new GovernedDigitalTwinExperimentRunner()).EvaluateAsync(definition);

        Assert.Equal(6, result.Ranking.Count);
        Assert.Equal(6 * 8 * 2, result.Runs.Count);
        Assert.All(result.Ranking, static item => Assert.True(item.HardConstraintQualified));
        Assert.All(result.Runs, static run => Assert.Equal(11, run.StageEvidence.Count));
        Assert.False(OptimizationGovernance.AutoProductionPolicyReplacementAllowed);
        Assert.False(OptimizationGovernance.ProductionAutomationAllowed);
    }

    private static OptimizationExperimentDefinition Definition(IReadOnlyList<OptimizationPolicyCandidate> candidates) => new()
    {
        ExperimentId = "p5-integration-exp",
        ScenarioSetVersion = "s0-s10-v1",
        SeedSet = [7],
        TopologyRevision = "p5-topology-r1",
        OrderDatasetVersion = "p5-orders-r1",
        PolicyCandidates = candidates,
        ObjectiveWeights = new OptimizationObjectiveWeights
        {
            Throughput = 2,
            MissionLeadTime = 2,
            WaitTime = 2,
            Energy = 1,
            Wear = 1,
            FailureRisk = 2,
            SlaViolation = 2,
            RecoveryTime = 1
        },
        ConstraintProfileHash = Constraint,
        SoftwareHead = Head
    };

    private static IReadOnlyList<OptimizationPolicyCandidate> ThreeCandidates() =>
        OptimizationPolicyCatalog.CreateStandardCandidates(Constraint, "p5-ci", Approval).Take(3).ToArray();
}
