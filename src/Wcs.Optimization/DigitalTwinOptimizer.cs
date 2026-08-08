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
        for (var round = 1; round <= OptimizationGovernance.DeterminismRoundsPerInput; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = await _runner.RunAsync(definition, policy, loadCase, seed, round, cancellationToken);
            ValidateRun(definition, policy, loadCase, seed, round, run);
            runs.Add(run);
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
        if (!OptimizationHash.IsSha256(definition.ScenarioEvidenceHash) ||
            !OptimizationHash.IsSha256(definition.TopologyEvidenceHash) ||
            !OptimizationHash.IsSha256(definition.OrderDatasetEvidenceHash) ||
            !OptimizationHash.IsSha256(definition.ObjectiveWeightsEvidenceHash))
            throw new InvalidOperationException("Scenario, topology, order dataset and objective-weight evidence must be SHA-256.");
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
            if (!OptimizationHash.IsSha256(policy.PolicyHash))
                throw new InvalidOperationException("Every policy must produce SHA-256 policy evidence.");
        }
        if (definition.SeedSet.Count == 0 || definition.SeedSet.Count > OptimizationGovernance.MaximumSeedCount ||
            definition.SeedSet.Distinct().Count() != definition.SeedSet.Count || definition.SeedSet.Any(static seed => seed == 0))
            throw new InvalidOperationException("SeedSet must be unique, non-zero and within the governed bound.");
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
        ValidateStageEvidence(run);
        ValidateMetrics(run.Metrics);
        if (!run.HardConstraintsSatisfied && string.IsNullOrWhiteSpace(run.FailureReason))
            throw new InvalidOperationException("Hard-constraint failure requires an immutable failure reason.");
        if (run.HardConstraintsSatisfied && !string.IsNullOrWhiteSpace(run.FailureReason))
            throw new InvalidOperationException("Successful hard-constraint evidence cannot carry a failure reason.");
    }

    private static void ValidateStageEvidence(OptimizationScenarioRun run)
    {
        if (run.StageEvidence.Count != OptimizationGovernance.RequiredSimulationStages.Length)
            throw new InvalidOperationException("Every optimizer run must contain exact S0-S10 stage evidence.");
        var expected = OptimizationGovernance.RequiredSimulationStages.OrderBy(static stage => stage).ToArray();
        var actual = run.StageEvidence.Select(static item => item.Stage).OrderBy(static stage => stage).ToArray();
        if (!expected.SequenceEqual(actual) || actual.Distinct().Count() != actual.Length)
            throw new InvalidOperationException("Optimizer stage evidence must contain each S0-S10 stage exactly once.");
        foreach (var item in run.StageEvidence)
        {
            if (!OptimizationHash.IsSha256(item.EvidenceHash))
                throw new InvalidOperationException($"Stage {item.Stage} evidence hash must be SHA-256.");
            if (item.RealHardwareExecuted && !item.RequiresRealHardware)
                throw new InvalidOperationException($"Stage {item.Stage} cannot claim real hardware execution.");
            if (item.Stage == OptimizationSimulationStage.S9HilSoftwareBoundary && item.RealHardwareExecuted)
                throw new InvalidOperationException("P5 must never execute or claim real HIL evidence.");
        }
        if (!run.StageEvidence.Where(static item => item.Stage != OptimizationSimulationStage.S9HilSoftwareBoundary)
            .All(static item => item.Available))
            throw new InvalidOperationException("S0-S8 and S10 must be available to a governed P5 experiment.");
        if (!run.StageEvidence.Where(static item => item.Stage is >= OptimizationSimulationStage.S1ScenarioEngine and <= OptimizationSimulationStage.S8CapacityReadiness)
            .All(static item => item.Executed))
            throw new InvalidOperationException("S1-S8 must execute inside the governed digital-twin experiment.");
        var s9 = run.StageEvidence.Single(static item => item.Stage == OptimizationSimulationStage.S9HilSoftwareBoundary);
        if (!s9.ReadOnlyBoundary || s9.Executed || !s9.RequiresRealHardware || s9.RealHardwareExecuted)
            throw new InvalidOperationException("S9 must remain a read-only software boundary with real hardware unexecuted.");
        if (run.HardConstraintsSatisfied != run.StageEvidence.All(static item => item.HardConstraintsSatisfied))
            throw new InvalidOperationException("Run hard-constraint state must match the conjunction of S0-S10 stage evidence.");
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
        foreach (var group in runs.GroupBy(static r => (r.PolicyId, r.LoadCase, r.Seed)))
        {
            var pair = group.OrderBy(static r => r.DeterminismRound).ToArray();
            if (pair.Length != OptimizationGovernance.DeterminismRoundsPerInput ||
                pair.Select(static item => item.DeterminismRound).SequenceEqual(Enumerable.Range(1, OptimizationGovernance.DeterminismRoundsPerInput)) == false ||
                !pair.Skip(1).All(item => string.Equals(pair[0].FinalStateHash, item.FinalStateHash, StringComparison.OrdinalIgnoreCase)) ||
                !pair.Skip(1).All(item => pair[0].Metrics == item.Metrics) ||
                !pair.Skip(1).All(item => item.HardConstraintsSatisfied == pair[0].HardConstraintsSatisfied))
                throw new InvalidOperationException($"Determinism replay mismatch for {group.Key.PolicyId}/{group.Key.LoadCase}/{group.Key.Seed}.");
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
            var failed = all.Length - ok.Length;
            var qualified = all.Length > 0 && failed == 0;
            var metrics = qualified ? Average(ok.Select(static r => r.Metrics).ToArray()) : new OptimizationMetrics();
            return new Aggregate(policy, metrics, ok.Length, failed, qualified);
        }).ToArray();
        var valid = aggregates.Where(static a => a.Qualified).ToArray();
        if (valid.Length == 0) throw new InvalidOperationException("No fully hard-constraint-qualified policy is available for ranking.");
        var bounds = Bounds.Create(valid.Select(static a => a.Metrics));
        return aggregates.Select(a => new OptimizationPolicyScore
        {
            PolicyId = a.Policy.PolicyId,
            PolicyHash = a.Policy.PolicyHash,
            Score = a.Qualified ? Score(a.Metrics, definition.ObjectiveWeights, bounds) : OptimizationGovernance.HardConstraintFailureScore,
            ParetoEfficient = a.Qualified && !valid.Any(other => !ReferenceEquals(other, a) && Dominates(other.Metrics, a.Metrics)),
            SuccessfulRuns = a.Successful,
            FailedRuns = a.Failed,
            HardConstraintQualified = a.Qualified,
            Aggregate = a.Metrics
        }).OrderByDescending(static x => x.HardConstraintQualified)
          .ThenByDescending(static x => x.Score)
          .ThenBy(static x => x.PolicyId, StringComparer.Ordinal)
          .ToArray();
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
        Throughput = x.Average(static m => m.Throughput),
        MeanMissionLeadTimeSeconds = x.Average(static m => m.MeanMissionLeadTimeSeconds),
        P95MissionLeadTimeSeconds = x.Average(static m => m.P95MissionLeadTimeSeconds),
        MeanWaitTimeSeconds = x.Average(static m => m.MeanWaitTimeSeconds),
        P95WaitTimeSeconds = x.Average(static m => m.P95WaitTimeSeconds),
        DeadlockCount = (int)Math.Round(x.Average(static m => m.DeadlockCount)),
        ConflictCount = (int)Math.Round(x.Average(static m => m.ConflictCount)),
        EnergyEstimate = x.Average(static m => m.EnergyEstimate),
        WearIndex = x.Average(static m => m.WearIndex),
        FailureRiskExposure = x.Average(static m => m.FailureRiskExposure),
        SlaViolationCount = (int)Math.Round(x.Average(static m => m.SlaViolationCount)),
        RecoveryTimeSeconds = x.Average(static m => m.RecoveryTimeSeconds)
    };

    private static bool IsExactGitHead(string? value) =>
        value is { Length: 40 or 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private sealed record Aggregate(OptimizationPolicyCandidate Policy, OptimizationMetrics Metrics, int Successful, int Failed, bool Qualified);
    private sealed record Range(double Min, double Max);

    private sealed class Bounds
    {
        public required Range Throughput { get; init; }
        public required Range Lead { get; init; }
        public required Range Wait { get; init; }
        public required Range Energy { get; init; }
        public required Range Wear { get; init; }
        public required Range Risk { get; init; }
        public required Range Sla { get; init; }
        public required Range Recovery { get; init; }

        public static Bounds Create(IEnumerable<OptimizationMetrics> source)
        {
            var x = source.ToArray();
            static Range R(IEnumerable<double> v)
            {
                var a = v.ToArray();
                return new(a.Min(), a.Max());
            }
            return new()
            {
                Throughput = R(x.Select(static m => m.Throughput)),
                Lead = R(x.Select(static m => m.P95MissionLeadTimeSeconds)),
                Wait = R(x.Select(static m => m.P95WaitTimeSeconds)),
                Energy = R(x.Select(static m => m.EnergyEstimate)),
                Wear = R(x.Select(static m => m.WearIndex)),
                Risk = R(x.Select(static m => m.FailureRiskExposure)),
                Sla = R(x.Select(static m => (double)m.SlaViolationCount)),
                Recovery = R(x.Select(static m => m.RecoveryTimeSeconds))
            };
        }

        public double Higher(double v, Range r) => Normalize(v, r);
        public double Lower(double v, Range r) => 1 - Normalize(v, r);
        private static double Normalize(double v, Range r) => Math.Abs(r.Max - r.Min) < 1e-12 ? 1 : (v - r.Min) / (r.Max - r.Min);
    }
}
