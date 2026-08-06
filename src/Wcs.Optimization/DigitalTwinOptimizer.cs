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

    public DigitalTwinOptimizer(IDigitalTwinExperimentRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<OptimizationExperimentResult> EvaluateAsync(
        OptimizationExperimentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        var runs = new List<OptimizationScenarioRun>();

        foreach (var policy in definition.PolicyCandidates)
        {
            foreach (var loadCase in OptimizationGovernance.RequiredLoadCases)
            {
                foreach (var seed in definition.SeedSet)
                {
                    var rounds = loadCase == OptimizationLoadCase.DeterminismReplay ? 2 : 1;
                    for (var round = 1; round <= rounds; round++)
                    {
                        var run = await _runner.RunAsync(
                            definition,
                            policy,
                            loadCase,
                            seed,
                            round,
                            cancellationToken);
                        ValidateRun(definition, policy, loadCase, seed, round, run);
                        runs.Add(run);
                    }
                }
            }
        }

        VerifyDeterminism(runs);
        var ranking = BuildRanking(definition, runs);
        return new OptimizationExperimentResult
        {
            ExperimentId = definition.ExperimentId,
            DefinitionHash = definition.DefinitionHash,
            SoftwareHead = definition.SoftwareHead,
            Ranking = ranking,
            Runs = runs,
            EvidenceHash = OptimizationHash.ComputeResultEvidenceHash(definition, runs)
        };
    }

    public static void ValidateDefinition(OptimizationExperimentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.ExperimentId))
            throw new InvalidOperationException("ExperimentId is required.");
        if (string.IsNullOrWhiteSpace(definition.ScenarioSetVersion) ||
            string.IsNullOrWhiteSpace(definition.TopologyRevision) ||
            string.IsNullOrWhiteSpace(definition.OrderDatasetVersion))
            throw new InvalidOperationException("ScenarioSetVersion, TopologyRevision and OrderDatasetVersion are required.");
        if (!OptimizationHash.IsSha256(definition.ConstraintProfileHash))
            throw new InvalidOperationException("ConstraintProfileHash must be SHA-256.");
        if (!OptimizationHash.IsSha256(definition.SoftwareHead))
            throw new InvalidOperationException("SoftwareHead must be the exact 64-character commit SHA.");

        if (definition.PolicyCandidates.Count < OptimizationGovernance.MinimumCandidateCount ||
            definition.PolicyCandidates.Count > OptimizationGovernance.MaximumCandidateCount)
            throw new InvalidOperationException(
                $"Policy candidate count must be within {OptimizationGovernance.MinimumCandidateCount}..{OptimizationGovernance.MaximumCandidateCount}.");
        if (definition.PolicyCandidates.Count(static p => p.Kind == OptimizationPolicyKind.CurrentProductionBaseline) != 1)
            throw new InvalidOperationException("Exactly one CurrentProductionBaseline policy is required.");
        if (definition.PolicyCandidates.Select(static p => p.PolicyId).Distinct(StringComparer.Ordinal).Count() != definition.PolicyCandidates.Count)
            throw new InvalidOperationException("PolicyId must be unique within one experiment.");

        foreach (var policy in definition.PolicyCandidates)
        {
            if (string.IsNullOrWhiteSpace(policy.PolicyId) || string.IsNullOrWhiteSpace(policy.Version))
                throw new InvalidOperationException("PolicyId and Version are required.");
            if (!OptimizationHash.IsSha256(policy.ConstraintProfileHash) ||
                !string.Equals(policy.ConstraintProfileHash, definition.ConstraintProfileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Every policy must use the exact governed ConstraintProfileHash.");
            if (string.IsNullOrWhiteSpace(policy.ApprovedBy) || policy.ApprovedAtUtc == default)
                throw new InvalidOperationException("Every policy requires explicit approval evidence.");
        }

        if (definition.SeedSet.Count == 0 || definition.SeedSet.Count > OptimizationGovernance.MaximumSeedCount)
            throw new InvalidOperationException($"SeedSet must contain 1..{OptimizationGovernance.MaximumSeedCount} seeds.");
        if (definition.SeedSet.Distinct().Count() != definition.SeedSet.Count)
            throw new InvalidOperationException("SeedSet cannot contain duplicates.");

        foreach (var weight in definition.ObjectiveWeights.AsOrderedValues())
        {
            if (!double.IsFinite(weight) || weight < 0 || weight > OptimizationGovernance.MaximumObjectiveWeight)
                throw new InvalidOperationException(
                    $"Objective weights must be finite and within 0..{OptimizationGovernance.MaximumObjectiveWeight}.");
        }
        if (definition.ObjectiveWeights.AsOrderedValues().All(static value => value == 0))
            throw new InvalidOperationException("At least one objective weight must be positive.");
    }

    private static void ValidateRun(
        OptimizationExperimentDefinition definition,
        OptimizationPolicyCandidate policy,
        OptimizationLoadCase loadCase,
        int seed,
        int round,
        OptimizationScenarioRun run)
    {
        if (!string.Equals(run.ExperimentId, definition.ExperimentId, StringComparison.Ordinal) ||
            !string.Equals(run.PolicyId, policy.PolicyId, StringComparison.Ordinal) ||
            !string.Equals(run.PolicyHash, policy.PolicyHash, StringComparison.OrdinalIgnoreCase) ||
            run.LoadCase != loadCase || run.Seed != seed || run.DeterminismRound != round ||
            !string.Equals(run.SoftwareHead, definition.SoftwareHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Experiment runner returned evidence for a different governed input.");
        if (!OptimizationHash.IsSha256(run.ScenarioHash) ||
            !OptimizationHash.IsSha256(run.FinalStateHash) ||
            !OptimizationHash.IsSha256(run.EvidenceHash))
            throw new InvalidOperationException("Scenario, final-state and run Evidence hashes must be SHA-256.");
        ValidateMetrics(run.Metrics);
    }

    private static void ValidateMetrics(OptimizationMetrics metrics)
    {
        var finite = new[]
        {
            metrics.Throughput,
            metrics.MeanMissionLeadTimeSeconds,
            metrics.P95MissionLeadTimeSeconds,
            metrics.MeanWaitTimeSeconds,
            metrics.P95WaitTimeSeconds,
            metrics.EnergyEstimate,
            metrics.WearIndex,
            metrics.FailureRiskExposure,
            metrics.RecoveryTimeSeconds
        };
        if (finite.Any(static value => !double.IsFinite(value) || value < 0))
            throw new InvalidOperationException("Optimization metrics must be finite and non-negative.");
        if (metrics.DeadlockCount < 0 || metrics.ConflictCount < 0 || metrics.SlaViolationCount < 0)
            throw new InvalidOperationException("Optimization count metrics cannot be negative.");
        if (metrics.P95MissionLeadTimeSeconds < metrics.MeanMissionLeadTimeSeconds ||
            metrics.P95WaitTimeSeconds < metrics.MeanWaitTimeSeconds)
            throw new InvalidOperationException("P95 metrics cannot be lower than their means.");
    }

    private static void VerifyDeterminism(IReadOnlyList<OptimizationScenarioRun> runs)
    {
        var replayGroups = runs
            .Where(static run => run.LoadCase == OptimizationLoadCase.DeterminismReplay)
            .GroupBy(run => (run.PolicyId, run.Seed));
        foreach (var group in replayGroups)
        {
            var ordered = group.OrderBy(static run => run.DeterminismRound).ToArray();
            if (ordered.Length != 2 || ordered[0].DeterminismRound != 1 || ordered[1].DeterminismRound != 2)
                throw new InvalidOperationException("DeterminismReplay requires exactly two rounds per policy/seed.");
            if (!string.Equals(ordered[0].FinalStateHash, ordered[1].FinalStateHash, StringComparison.OrdinalIgnoreCase) ||
                !MetricsEqual(ordered[0].Metrics, ordered[1].Metrics))
                throw new InvalidOperationException(
                    $"Determinism replay mismatch for policy {group.Key.PolicyId}, seed {group.Key.Seed}.");
        }
    }

    private static IReadOnlyList<OptimizationPolicyScore> BuildRanking(
        OptimizationExperimentDefinition definition,
        IReadOnlyList<OptimizationScenarioRun> runs)
    {
        var aggregates = definition.PolicyCandidates.Select(policy =>
        {
            var policyRuns = runs.Where(run => string.Equals(run.PolicyId, policy.PolicyId, StringComparison.Ordinal)).ToArray();
            var successful = policyRuns.Where(static run => run.HardConstraintsSatisfied).ToArray();
            if (successful.Length == 0)
                return new PolicyAggregate(policy, new OptimizationMetrics(), 0, policyRuns.Length, false);
            return new PolicyAggregate(
                policy,
                Average(successful.Select(static run => run.Metrics).ToArray()),
                successful.Length,
                policyRuns.Length - successful.Length,
                true);
        }).ToArray();

        var valid = aggregates.Where(static value => value.HasMetrics).ToArray();
        if (valid.Length == 0)
            throw new InvalidOperationException("No candidate has complete hard-constraint-satisfying metrics; ranking is unavailable.");

        var bounds = MetricBounds.Create(valid.Select(static value => value.Metrics));
        var scored = aggregates.Select(value => new OptimizationPolicyScore
        {
            PolicyId = value.Policy.PolicyId,
            PolicyHash = value.Policy.PolicyHash,
            Score = value.HasMetrics ? Score(value.Metrics, definition.ObjectiveWeights, bounds) : double.NegativeInfinity,
            ParetoEfficient = value.HasMetrics && IsParetoEfficient(value, valid),
            SuccessfulRuns = value.SuccessfulRuns,
            FailedRuns = value.FailedRuns,
            Aggregate = value.Metrics
        })
        .OrderByDescending(static value => value.Score)
        .ThenBy(static value => value.PolicyId, StringComparer.Ordinal)
        .ToArray();
        return scored;
    }

    private static double Score(OptimizationMetrics m, OptimizationObjectiveWeights w, MetricBounds b) =>
        w.Throughput * b.NormalizeHigher(m.Throughput, b.Throughput) +
        w.MissionLeadTime * b.NormalizeLower(m.P95MissionLeadTimeSeconds, b.MissionLeadTime) +
        w.WaitTime * b.NormalizeLower(m.P95WaitTimeSeconds, b.WaitTime) +
        w.Energy * b.NormalizeLower(m.EnergyEstimate, b.Energy) +
        w.Wear * b.NormalizeLower(m.WearIndex, b.Wear) +
        w.FailureRisk * b.NormalizeLower(m.FailureRiskExposure, b.FailureRisk) +
        w.SlaViolation * b.NormalizeLower(m.SlaViolationCount, b.SlaViolation) +
        w.RecoveryTime * b.NormalizeLower(m.RecoveryTimeSeconds, b.RecoveryTime);

    private static bool IsParetoEfficient(PolicyAggregate candidate, IReadOnlyList<PolicyAggregate> all) =>
        !all.Any(other => !ReferenceEquals(other, candidate) && Dominates(other.Metrics, candidate.Metrics));

    private static bool Dominates(OptimizationMetrics a, OptimizationMetrics b)
    {
        var noWorse = a.Throughput >= b.Throughput &&
                      a.P95MissionLeadTimeSeconds <= b.P95MissionLeadTimeSeconds &&
                      a.P95WaitTimeSeconds <= b.P95WaitTimeSeconds &&
                      a.DeadlockCount <= b.DeadlockCount &&
                      a.ConflictCount <= b.ConflictCount &&
                      a.EnergyEstimate <= b.EnergyEstimate &&
                      a.WearIndex <= b.WearIndex &&
                      a.FailureRiskExposure <= b.FailureRiskExposure &&
                      a.SlaViolationCount <= b.SlaViolationCount &&
                      a.RecoveryTimeSeconds <= b.RecoveryTimeSeconds;
        var strictlyBetter = a.Throughput > b.Throughput ||
                             a.P95MissionLeadTimeSeconds < b.P95MissionLeadTimeSeconds ||
                             a.P95WaitTimeSeconds < b.P95WaitTimeSeconds ||
                             a.DeadlockCount < b.DeadlockCount ||
                             a.ConflictCount < b.ConflictCount ||
                             a.EnergyEstimate < b.EnergyEstimate ||
                             a.WearIndex < b.WearIndex ||
                             a.FailureRiskExposure < b.FailureRiskExposure ||
                             a.SlaViolationCount < b.SlaViolationCount ||
                             a.RecoveryTimeSeconds < b.RecoveryTimeSeconds;
        return noWorse && strictlyBetter;
    }

    private static OptimizationMetrics Average(IReadOnlyList<OptimizationMetrics> values) => new()
    {
        Throughput = values.Average(static x => x.Throughput),
        MeanMissionLeadTimeSeconds = values.Average(static x => x.MeanMissionLeadTimeSeconds),
        P95MissionLeadTimeSeconds = values.Average(static x => x.P95MissionLeadTimeSeconds),
        MeanWaitTimeSeconds = values.Average(static x => x.MeanWaitTimeSeconds),
        P95WaitTimeSeconds = values.Average(static x => x.P95WaitTimeSeconds),
        DeadlockCount = (int)Math.Round(values.Average(static x => x.DeadlockCount)),
        ConflictCount = (int)Math.Round(values.Average(static x => x.ConflictCount)),
        EnergyEstimate = values.Average(static x => x.EnergyEstimate),
        WearIndex = values.Average(static x => x.WearIndex),
        FailureRiskExposure = values.Average(static x => x.FailureRiskExposure),
        SlaViolationCount = (int)Math.Round(values.Average(static x => x.SlaViolationCount)),
        RecoveryTimeSeconds = values.Average(static x => x.RecoveryTimeSeconds)
    };

    private static bool MetricsEqual(OptimizationMetrics a, OptimizationMetrics b) =>
        a == b;

    private sealed record PolicyAggregate(
        OptimizationPolicyCandidate Policy,
        OptimizationMetrics Metrics,
        int SuccessfulRuns,
        int FailedRuns,
        bool HasMetrics);

    private sealed record Range(double Min, double Max);

    private sealed class MetricBounds
    {
        public required Range Throughput { get; init; }
        public required Range MissionLeadTime { get; init; }
        public required Range WaitTime { get; init; }
        public required Range Energy { get; init; }
        public required Range Wear { get; init; }
        public required Range FailureRisk { get; init; }
        public required Range SlaViolation { get; init; }
        public required Range RecoveryTime { get; init; }

        public static MetricBounds Create(IEnumerable<OptimizationMetrics> source)
        {
            var values = source.ToArray();
            return new MetricBounds
            {
                Throughput = Of(values.Select(static x => x.Throughput)),
                MissionLeadTime = Of(values.Select(static x => x.P95MissionLeadTimeSeconds)),
                WaitTime = Of(values.Select(static x => x.P95WaitTimeSeconds)),
                Energy = Of(values.Select(static x => x.EnergyEstimate)),
                Wear = Of(values.Select(static x => x.WearIndex)),
                FailureRisk = Of(values.Select(static x => x.FailureRiskExposure)),
                SlaViolation = Of(values.Select(static x => (double)x.SlaViolationCount)),
                RecoveryTime = Of(values.Select(static x => x.RecoveryTimeSeconds))
            };
        }

        public double NormalizeHigher(double value, Range range) => Normalize(value, range);
        public double NormalizeLower(double value, Range range) => 1 - Normalize(value, range);

        private static Range Of(IEnumerable<double> values)
        {
            var array = values.ToArray();
            return new Range(array.Min(), array.Max());
        }

        private static double Normalize(double value, Range range) =>
            Math.Abs(range.Max - range.Min) < 1e-12 ? 1 : (value - range.Min) / (range.Max - range.Min);
    }
}
