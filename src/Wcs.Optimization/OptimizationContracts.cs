namespace Wcs.Optimization;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

public enum OptimizationPolicyKind
{
    CurrentProductionBaseline = 0,
    ShortestDistance = 1,
    HealthAware = 2,
    EnergyAware = 3,
    SlaAware = 4,
    BalancedMultiObjective = 5
}

public enum OptimizationLoadCase
{
    NormalLoad = 0,
    PeakLoad = 1,
    SingleVehicleDegraded = 2,
    SegmentBlocked = 3,
    ExternalDependencyFailure = 4,
    HealthDegraded = 5,
    RestartRecovery = 6,
    DeterminismReplay = 7
}

public sealed record OptimizationObjectiveWeights
{
    public double Throughput { get; init; } = 1;
    public double MissionLeadTime { get; init; } = 1;
    public double WaitTime { get; init; } = 1;
    public double Energy { get; init; } = 1;
    public double Wear { get; init; } = 1;
    public double FailureRisk { get; init; } = 1;
    public double SlaViolation { get; init; } = 1;
    public double RecoveryTime { get; init; } = 1;

    public IReadOnlyList<double> AsOrderedValues() =>
    [Throughput, MissionLeadTime, WaitTime, Energy, Wear, FailureRisk, SlaViolation, RecoveryTime];
}

public sealed record OptimizationPolicyCandidate
{
    public required string PolicyId { get; init; }
    public required string Version { get; init; }
    public required OptimizationPolicyKind Kind { get; init; }
    public required string ConstraintProfileHash { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTime ApprovedAtUtc { get; init; }
    public string PolicyHash => OptimizationHash.ComputePolicyHash(this);
}

public sealed record OptimizationExperimentDefinition
{
    public required string ExperimentId { get; init; }
    public required string ScenarioSetVersion { get; init; }
    public required IReadOnlyList<int> SeedSet { get; init; }
    public required string TopologyRevision { get; init; }
    public required string OrderDatasetVersion { get; init; }
    public required IReadOnlyList<OptimizationPolicyCandidate> PolicyCandidates { get; init; }
    public required OptimizationObjectiveWeights ObjectiveWeights { get; init; }
    public required string ConstraintProfileHash { get; init; }
    public required string SoftwareHead { get; init; }
    public string DefinitionHash => OptimizationHash.ComputeExperimentHash(this);
}

public sealed record OptimizationMetrics
{
    public double Throughput { get; init; }
    public double MeanMissionLeadTimeSeconds { get; init; }
    public double P95MissionLeadTimeSeconds { get; init; }
    public double MeanWaitTimeSeconds { get; init; }
    public double P95WaitTimeSeconds { get; init; }
    public int DeadlockCount { get; init; }
    public int ConflictCount { get; init; }
    public double EnergyEstimate { get; init; }
    public double WearIndex { get; init; }
    public double FailureRiskExposure { get; init; }
    public int SlaViolationCount { get; init; }
    public double RecoveryTimeSeconds { get; init; }
}

public sealed record OptimizationScenarioRun
{
    public required string ExperimentId { get; init; }
    public required string PolicyId { get; init; }
    public required string PolicyHash { get; init; }
    public required OptimizationLoadCase LoadCase { get; init; }
    public required int Seed { get; init; }
    public required int DeterminismRound { get; init; }
    public required string ScenarioHash { get; init; }
    public required string SoftwareHead { get; init; }
    public required OptimizationMetrics Metrics { get; init; }
    public required string FinalStateHash { get; init; }
    public required string EvidenceHash { get; init; }
    public bool HardConstraintsSatisfied { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record OptimizationPolicyScore
{
    public required string PolicyId { get; init; }
    public required string PolicyHash { get; init; }
    public required double Score { get; init; }
    public required bool ParetoEfficient { get; init; }
    public required int SuccessfulRuns { get; init; }
    public required int FailedRuns { get; init; }
    public required OptimizationMetrics Aggregate { get; init; }
}

public sealed record OptimizationExperimentResult
{
    public required string ExperimentId { get; init; }
    public required string DefinitionHash { get; init; }
    public required string SoftwareHead { get; init; }
    public required IReadOnlyList<OptimizationPolicyScore> Ranking { get; init; }
    public required IReadOnlyList<OptimizationScenarioRun> Runs { get; init; }
    public required string EvidenceHash { get; init; }
    public bool ControlWriteAllowed => false;
    public bool AutoProductionPolicyReplacementAllowed => false;
}

public static class OptimizationGovernance
{
    public const int MinimumCandidateCount = 3;
    public const int MaximumCandidateCount = 12;
    public const int MaximumSeedCount = 32;
    public const double MaximumObjectiveWeight = 100;
    public static bool ControlWriteAllowed => false;
    public static bool AutoProductionPolicyReplacementAllowed => false;
    public static string MaximumAutomationLevel => "L1";

    public static readonly OptimizationLoadCase[] RequiredLoadCases =
    [
        OptimizationLoadCase.NormalLoad,
        OptimizationLoadCase.PeakLoad,
        OptimizationLoadCase.SingleVehicleDegraded,
        OptimizationLoadCase.SegmentBlocked,
        OptimizationLoadCase.ExternalDependencyFailure,
        OptimizationLoadCase.HealthDegraded,
        OptimizationLoadCase.RestartRecovery,
        OptimizationLoadCase.DeterminismReplay
    ];
}

public static class OptimizationHash
{
    public static string ComputePolicyHash(OptimizationPolicyCandidate policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Sha256(string.Join("\n",
            Normalize(policy.PolicyId),
            Normalize(policy.Version),
            ((int)policy.Kind).ToString(CultureInfo.InvariantCulture),
            NormalizeHash(policy.ConstraintProfileHash),
            Normalize(policy.ApprovedBy),
            policy.ApprovedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }

    public static string ComputeExperimentHash(OptimizationExperimentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var policies = definition.PolicyCandidates
            .Select(static p => p.PolicyHash)
            .OrderBy(static value => value, StringComparer.Ordinal);
        var seeds = definition.SeedSet.OrderBy(static value => value)
            .Select(static value => value.ToString(CultureInfo.InvariantCulture));
        var weights = definition.ObjectiveWeights.AsOrderedValues()
            .Select(static value => value.ToString("R", CultureInfo.InvariantCulture));
        return Sha256(string.Join("\n",
            Normalize(definition.ExperimentId),
            Normalize(definition.ScenarioSetVersion),
            Normalize(definition.TopologyRevision),
            Normalize(definition.OrderDatasetVersion),
            NormalizeHash(definition.ConstraintProfileHash),
            NormalizeHash(definition.SoftwareHead),
            string.Join(',', seeds),
            string.Join(',', policies),
            string.Join(',', weights)));
    }

    public static string ComputeResultEvidenceHash(
        OptimizationExperimentDefinition definition,
        IEnumerable<OptimizationScenarioRun> runs)
    {
        var canonicalRuns = runs
            .OrderBy(static run => run.PolicyId, StringComparer.Ordinal)
            .ThenBy(static run => run.LoadCase)
            .ThenBy(static run => run.Seed)
            .ThenBy(static run => run.DeterminismRound)
            .Select(run => string.Join('|',
                Normalize(run.PolicyId), NormalizeHash(run.PolicyHash), (int)run.LoadCase,
                run.Seed, run.DeterminismRound, NormalizeHash(run.ScenarioHash),
                NormalizeHash(run.FinalStateHash), NormalizeHash(run.EvidenceHash),
                run.HardConstraintsSatisfied ? "1" : "0"));
        return Sha256(definition.DefinitionHash + "\n" + string.Join("\n", canonicalRuns));
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeHash(string value) => Normalize(value).ToLowerInvariant();

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
