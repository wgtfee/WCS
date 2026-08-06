namespace Wcs.Optimization;

public interface IDigitalTwinExperimentRunner
{
    Task<OptimizationScenarioRun> RunAsync(
        OptimizationExperimentDefinition definition,
        OptimizationPolicyCandidate policy,
        OptimizationLoadCase loadCase,
        int seed,
        int determinismRound,
        CancellationToken cancellationToken);
}

public sealed class DigitalTwinOptimizer
{
    private readonly IDigitalTwinExperimentRunner _runner;

    public DigitalTwinOptimizer(IDigitalTwinExperimentRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public async Task<OptimizationExperimentResult> EvaluateAsync(
        OptimizationExperimentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        var runs = new List<OptimizationScenarioRun>();
        foreach (var policy in definition.PolicyCandidates)
        foreach (var loadCase in OptimizationGovernance.RequiredLoadCases)
        foreach (var seed in definition.SeedSet)
        {
            var rounds = loadCase == OptimizationLoadCase.DeterminismReplay ? 2 : 1;
            for (var round = 1; round <= rounds; round++)
            {
                var run = await _runner.RunAsync(definition, policy, loadCase, seed, round, cancellationToken);
                ValidateRun(definition, policy, loadCase, seed, round, run);
                runs.Add(run);
            }
        }

        VerifyDeterminism(runs);
        return new OptimizationExperimentResult
        {
            ExperimentId = definition.ExperimentId,
            DefinitionHash = definition.DefinitionHash,
            SoftwareHead = definition.SoftwareHead,
            Ranking = BuildRanking(definition, runs),
            Runs = runs,
            EvidenceHash = OptimizationHash.ComputeResultEvidenceHash(definition, runs)
        };
    }

    public static void ValidateDefinition(OptimizationExperimentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.ExperimentId) ||
            string.IsNullOrWhiteSpace(definition.ScenarioSetVersion) ||
            string.IsNullOrWhiteSpace(definition.TopologyRevision) ||
            string.IsNullOrWhiteSpace(definition.OrderDatasetVersion))
            throw new InvalidOperationException("Experiment identity, scenario, topology and order dataset versions are required.");
        if (!OptimizationHash.IsSha256(definition.ConstraintProfileHash))
            throw new InvalidOperationException("ConstraintProfileHash must be SHA-256.");
        if (!IsExactGitHead(definition.SoftwareHead))
            throw new InvalidOperationException("SoftwareHead must be an exact Git commit SHA (40 or 64 hex characters).");
        if (definition.PolicyCandidates.Count is < OptimizationGovernance.MinimumCandidateCount or > OptimizationGovernance.MaximumCandidateCount)
            throw new InvalidOperationException("Policy candidate count is outside the governed bound.");
        if (definition.PolicyCandidates.Count(static p => p.Kind == OptimizationPolicyKind.CurrentProductionBaseline) != 1)
            throw new InvalidOperationException("Exactly one CurrentProductionBaseline policy is required.");
        if (definition.PolicyCandidates.Select(static p => p.PolicyId).Distinct(StringComparer.Ordinal).Count() != definition.PolicyCandidates.Count)
            throw new InvalidOperationException("PolicyId must be unique within one experiment.");
        foreach (var policy in definition.PolicyCandidates)
        {
            if (string.IsNullOrWhiteSpace(policy.PolicyId) || string.IsNullOrWhiteSpace(policy.Version) ||
                string.IsNullOrWhiteSpace(policy.ApprovedBy) || policy.ApprovedAtUtc == default)
                throw new InvalidOperationException("Every policy requires identity, version and explicit approval evidence.");
            if (!OptimizationHash.IsSha256(policy.ConstraintProfileHash) ||
                !string.Equals(policy.ConstraintProfileHash, definition.ConstraintProfileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Every policy must use the exact governed ConstraintProfileHash.");
        }
        if (definition.SeedSet.Count == 0 || definition.SeedSet.Count > OptimizationGovernance.MaximumSeedCount ||
            definition.SeedSet.Distinct().Count() != definition.SeedSet.Count)
            throw new InvalidOperationException("SeedSet must be unique and within the governed bound.");
        var weights = definition.ObjectiveWeights.AsOrderedValues();
        if (weights.Any(static w => !double.IsFinite(w) || w < 0 || w > OptimizationGovernance.MaximumObjectiveWeight) ||
            weights.All(static w => w == 0))
            throw new InvalidOperationException("Objective weights must be finite, bounded and contain at least one positive objective.");
    }

    private static void ValidateRun(
        OptimizationExperimentDefinition definition,
        OptimizationPolicyCandidate policy,
        OptimizationLoadCase loadCase,
        int seed,
        int round,
        OptimizationScenarioRun run)
    {
        if (run.ExperimentId != definition.ExperimentId || run.PolicyId != policy.PolicyId ||
            !string.Equals(run.PolicyHash, policy.PolicyHash, StringComparison.OrdinalIgnoreCase) ||
            run.LoadCase != loadCase || run.Seed != seed || run.DeterminismRound != round ||
            !string.Equals(run.SoftwareHead, definition.SoftwareHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Experiment runner returned evidence for a different governed input.");
        if (!OptimizationHash.IsSha256(run.ScenarioHash) || !OptimizationHash.IsSha256(run.FinalStateHash) ||
            !OptimizationHash.IsSha256(run.EvidenceHash))
            throw new InvalidOperationException("Scenario, final-state and run evidence hashes must be SHA-256.");
        ValidateMetrics(run.Metrics);
    }

    private static void ValidateMetrics(OptimizationMetrics m)
    {
        var values = new[] { m.Throughput, m.MeanMissionLeadTimeSeconds, m.P95MissionLeadTimeSeconds,
            m.MeanWaitTimeSeconds, m.P95WaitTimeSeconds, m.EnergyEstimate, m.WearIndex,
            m.FailureRiskExposure, m.RecoveryTimeSeconds };
        if (values.Any(static v => !double.IsFinite(v) || v < 0) ||
            m.DeadlockCount < 0 || m.ConflictCount < 0 || m.SlaViolationCount < 0 ||
            m.P95MissionLeadTimeSeconds < m.MeanMissionLeadTimeSeconds ||
            m.P95WaitTimeSeconds < m.MeanWaitTimeSeconds)
            throw new InvalidOperationException("Optimization metrics are invalid or incomplete.");
    }

    private static void VerifyDeterminism(IEnumerable<OptimizationScenarioRun> runs)
    {
        foreach (var group in runs.Where(static r => r.LoadCase == OptimizationLoadCase.DeterminismReplay)
                     .GroupBy(static r => (r.PolicyId, r.Seed)))
        {
            var pair = group.OrderBy(static r => r.DeterminismRound).ToArray();
            if (pair.Length != 2 || pair[0].DeterminismRound != 1 || pair[1].DeterminismRound != 2 ||
                !string.Equals(pair[0].FinalStateHash, pair[1].FinalStateHash, StringComparison.OrdinalIgnoreCase) ||
                pair[0].Metrics != pair[1].Metrics)
                throw new InvalidOperationException($"Determinism replay mismatch for {group.Key.PolicyId}/{group.Key.Seed}.");
        }
    }

    private static IReadOnlyList<OptimizationPolicyScore> BuildRanking(
        OptimizationExperimentDefinition definition,
        IReadOnlyList<OptimizationScenarioRun> runs)
    {
        var aggregates = definition.PolicyCandidates.Select(policy =>
        {
            var all = runs.Where(r => r.PolicyId == policy.PolicyId).ToArray();
            var ok = all.Where(static r => r.HardConstraintsSatisfied).ToArray();
            return new Aggregate(policy, ok.Length == 0 ? new OptimizationMetrics() : Average(ok.Select(static r => r.Metrics).ToArray()),
                ok.Length, all.Length - ok.Length, ok.Length > 0);
        }).ToArray();
        var valid = aggregates.Where(static a => a.Valid).ToArray();
        if (valid.Length == 0) throw new InvalidOperationException("No hard-constraint-satisfying metrics are available for ranking.");
        var bounds = Bounds.Create(valid.Select(static a => a.Metrics));
        return aggregates.Select(a => new OptimizationPolicyScore
        {
            PolicyId = a.Policy.PolicyId,
            PolicyHash = a.Policy.PolicyHash,
            Score = a.Valid ? Score(a.Metrics, definition.ObjectiveWeights, bounds) : double.NegativeInfinity,
            ParetoEfficient = a.Valid && !valid.Any(other => !ReferenceEquals(other, a) && Dominates(other.Metrics, a.Metrics)),
            SuccessfulRuns = a.Successful,
            FailedRuns = a.Failed,
            Aggregate = a.Metrics
        }).OrderByDescending(static x => x.Score).ThenBy(static x => x.PolicyId, StringComparer.Ordinal).ToArray();
    }

    private static double Score(OptimizationMetrics m, OptimizationObjectiveWeights w, Bounds b) =>
        w.Throughput * b.Higher(m.Throughput, b.Throughput) +
        w.MissionLeadTime * b.Lower(m.P95MissionLeadTimeSeconds, b.Lead) +
        w.WaitTime * b.Lower(m.P95WaitTimeSeconds, b.Wait) +
        w.Energy * b.Lower(m.EnergyEstimate, b.Energy) +
        w.Wear * b.Lower(m.WearIndex, b.Wear) +
        w.FailureRisk * b.Lower(m.FailureRiskExposure, b.Risk) +
        w.SlaViolation * b.Lower(m.SlaViolationCount, b.Sla) +
        w.RecoveryTime * b.Lower(m.RecoveryTimeSeconds, b.Recovery);

    private static bool Dominates(OptimizationMetrics a, OptimizationMetrics b)
    {
        var noWorse = a.Throughput >= b.Throughput && a.P95MissionLeadTimeSeconds <= b.P95MissionLeadTimeSeconds &&
            a.P95WaitTimeSeconds <= b.P95WaitTimeSeconds && a.DeadlockCount <= b.DeadlockCount &&
            a.ConflictCount <= b.ConflictCount && a.EnergyEstimate <= b.EnergyEstimate && a.WearIndex <= b.WearIndex &&
            a.FailureRiskExposure <= b.FailureRiskExposure && a.SlaViolationCount <= b.SlaViolationCount &&
            a.RecoveryTimeSeconds <= b.RecoveryTimeSeconds;
        var better = a.Throughput > b.Throughput || a.P95MissionLeadTimeSeconds < b.P95MissionLeadTimeSeconds ||
            a.P95WaitTimeSeconds < b.P95WaitTimeSeconds || a.DeadlockCount < b.DeadlockCount ||
            a.ConflictCount < b.ConflictCount || a.EnergyEstimate < b.EnergyEstimate || a.WearIndex < b.WearIndex ||
            a.FailureRiskExposure < b.FailureRiskExposure || a.SlaViolationCount < b.SlaViolationCount ||
            a.RecoveryTimeSeconds < b.RecoveryTimeSeconds;
        return noWorse && better;
    }

    private static OptimizationMetrics Average(IReadOnlyList<OptimizationMetrics> x) => new()
    {
        Throughput = x.Average(static m => m.Throughput), MeanMissionLeadTimeSeconds = x.Average(static m => m.MeanMissionLeadTimeSeconds),
        P95MissionLeadTimeSeconds = x.Average(static m => m.P95MissionLeadTimeSeconds), MeanWaitTimeSeconds = x.Average(static m => m.MeanWaitTimeSeconds),
        P95WaitTimeSeconds = x.Average(static m => m.P95WaitTimeSeconds), DeadlockCount = (int)Math.Round(x.Average(static m => m.DeadlockCount)),
        ConflictCount = (int)Math.Round(x.Average(static m => m.ConflictCount)), EnergyEstimate = x.Average(static m => m.EnergyEstimate),
        WearIndex = x.Average(static m => m.WearIndex), FailureRiskExposure = x.Average(static m => m.FailureRiskExposure),
        SlaViolationCount = (int)Math.Round(x.Average(static m => m.SlaViolationCount)), RecoveryTimeSeconds = x.Average(static m => m.RecoveryTimeSeconds)
    };

    private static bool IsExactGitHead(string? value) =>
        value is { Length: 40 or 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private sealed record Aggregate(OptimizationPolicyCandidate Policy, OptimizationMetrics Metrics, int Successful, int Failed, bool Valid);
    private sealed record Range(double Min, double Max);
    private sealed class Bounds
    {
        public required Range Throughput { get; init; } public required Range Lead { get; init; } public required Range Wait { get; init; }
        public required Range Energy { get; init; } public required Range Wear { get; init; } public required Range Risk { get; init; }
        public required Range Sla { get; init; } public required Range Recovery { get; init; }
        public static Bounds Create(IEnumerable<OptimizationMetrics> source)
        {
            var x = source.ToArray();
            static Range R(IEnumerable<double> v) { var a = v.ToArray(); return new(a.Min(), a.Max()); }
            return new() { Throughput=R(x.Select(static m=>m.Throughput)), Lead=R(x.Select(static m=>m.P95MissionLeadTimeSeconds)),
                Wait=R(x.Select(static m=>m.P95WaitTimeSeconds)), Energy=R(x.Select(static m=>m.EnergyEstimate)), Wear=R(x.Select(static m=>m.WearIndex)),
                Risk=R(x.Select(static m=>m.FailureRiskExposure)), Sla=R(x.Select(static m=>(double)m.SlaViolationCount)), Recovery=R(x.Select(static m=>m.RecoveryTimeSeconds)) };
        }
        public double Higher(double v, Range r) => N(v,r); public double Lower(double v, Range r) => 1-N(v,r);
        private static double N(double v, Range r) => Math.Abs(r.Max-r.Min)<1e-12 ? 1 : (v-r.Min)/(r.Max-r.Min);
    }
}
