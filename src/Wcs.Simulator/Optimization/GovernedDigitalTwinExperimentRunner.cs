namespace Wcs.Simulator.Optimization;

using System.Security.Cryptography;
using System.Text;
using Wcs.Optimization;
using Wcs.Simulator.CapacityReadiness;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualIntegration;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

/// <summary>
/// P5 software-only experiment runner. It executes the existing S1-S8 deterministic
/// simulation stack through CapacityReadinessRuntime, records S0/S10 as governed read-only
/// boundaries, and records S9 strictly as a non-executed real-HIL boundary.
/// No production PLC, command, scheduler, orchestrator, device, dispatch, traffic or route
/// reservation control source is referenced by this runner.
/// </summary>
public sealed class GovernedDigitalTwinExperimentRunner : IDigitalTwinExperimentRunner
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);

    public Task<OptimizationScenarioRun> RunAsync(
        OptimizationExperimentDefinition definition,
        OptimizationPolicyCandidate policy,
        OptimizationLoadCase loadCase,
        int seed,
        int determinismRound,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DigitalTwinOptimizer.ValidateDefinition(definition);
        if (!definition.PolicyCandidates.Any(item => string.Equals(item.PolicyId, policy.PolicyId, StringComparison.Ordinal) &&
                                                     string.Equals(item.PolicyHash, policy.PolicyHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Policy candidate does not belong to the governed experiment definition.");
        if (!OptimizationGovernance.RequiredLoadCases.Contains(loadCase))
            throw new InvalidOperationException("Load case is outside the governed P5 experiment set.");
        if (!definition.SeedSet.Contains(seed))
            throw new InvalidOperationException("Seed does not belong to the governed experiment definition.");
        if (determinismRound is < 1 or > OptimizationGovernance.DeterminismRoundsPerInput)
            throw new InvalidOperationException("Determinism round is outside the governed bound.");

        var scenarioHash = BuildScenarioHash(definition, loadCase, seed);
        var state = new SimulationStateStore(EngineOptions());
        var capacityRuntime = CapacityRuntime(state);
        var profile = Profile(definition, loadCase, seed);
        var report = capacityRuntime.Run(profile, Start(seed, loadCase));

        var hardConstraintsSatisfied = report.Admission.Accepted &&
                                       report.Profile.ConservationSatisfied &&
                                       report.Profile.BoundedStateSatisfied &&
                                       report.Profile.State == CapacityProfileState.Completed &&
                                       OptimizationGovernance.ControlWriteAllowed == false &&
                                       OptimizationGovernance.AutoProductionPolicyReplacementAllowed == false &&
                                       OptimizationGovernance.ProductionAutomationAllowed == false;
        var failureReason = hardConstraintsSatisfied
            ? null
            : string.Join(';', report.Admission.Violations.DefaultIfEmpty(report.Profile.Detail ?? "simulation-hard-constraint"));

        var metrics = ProjectMetrics(policy.Kind, loadCase, profile, report);
        var finalStateHash = Hash(string.Join("\n",
            report.Profile.FinalStateHash,
            scenarioHash,
            policy.PolicyHash,
            ((int)loadCase).ToString(),
            seed.ToString(),
            hardConstraintsSatisfied ? "hard-constraints:pass" : "hard-constraints:fail"));
        var stages = BuildStageEvidence(definition, policy, loadCase, seed, determinismRound, report, hardConstraintsSatisfied);
        var evidenceHash = Hash(string.Join("\n",
            definition.DefinitionHash,
            definition.ObjectiveWeightsEvidenceHash,
            definition.ScenarioEvidenceHash,
            definition.TopologyEvidenceHash,
            definition.OrderDatasetEvidenceHash,
            definition.ConstraintProfileHash,
            policy.PolicyHash,
            scenarioHash,
            report.Profile.EvidenceHash,
            finalStateHash,
            string.Join(',', stages.OrderBy(static item => item.Stage).Select(static item => item.EvidenceHash))));

        return Task.FromResult(new OptimizationScenarioRun
        {
            ExperimentId = definition.ExperimentId,
            PolicyId = policy.PolicyId,
            PolicyHash = policy.PolicyHash,
            LoadCase = loadCase,
            Seed = seed,
            DeterminismRound = determinismRound,
            ScenarioHash = scenarioHash,
            SoftwareHead = definition.SoftwareHead,
            Metrics = metrics,
            FinalStateHash = finalStateHash,
            EvidenceHash = evidenceHash,
            StageEvidence = stages,
            HardConstraintsSatisfied = hardConstraintsSatisfied,
            FailureReason = failureReason
        });
    }

    private static CapacityProfileDefinition Profile(
        OptimizationExperimentDefinition definition,
        OptimizationLoadCase loadCase,
        int seed)
    {
        var kind = loadCase == OptimizationLoadCase.PeakLoad ? CapacityProfileKind.Peak : CapacityProfileKind.Nominal;
        var missionCount = loadCase switch
        {
            OptimizationLoadCase.PeakLoad => 12,
            OptimizationLoadCase.RestartRecovery => 8,
            OptimizationLoadCase.DeterminismReplay => 8,
            _ => 6
        };
        var concurrent = loadCase == OptimizationLoadCase.PeakLoad ? 4 : 2;
        var duration = loadCase == OptimizationLoadCase.PeakLoad ? 120_000L : 60_000L;
        var id = $"p5-{Short(definition.DefinitionHash)}-{(int)loadCase}-{Math.Abs((long)seed)}";
        return new CapacityProfileDefinition(id, kind, missionCount, concurrent, 2, duration);
    }

    private static DateTimeOffset Start(int seed, OptimizationLoadCase loadCase)
    {
        var bounded = Math.Abs((long)seed % 10_000_000L);
        return Epoch.AddMilliseconds(bounded + (int)loadCase * 100_000L);
    }

    private static OptimizationMetrics ProjectMetrics(
        OptimizationPolicyKind kind,
        OptimizationLoadCase loadCase,
        CapacityProfileDefinition profile,
        CapacityRunReport report)
    {
        var loadFactor = loadCase switch
        {
            OptimizationLoadCase.PeakLoad => 1.35,
            OptimizationLoadCase.SingleVehicleDegraded => 1.20,
            OptimizationLoadCase.SegmentBlocked => 1.30,
            OptimizationLoadCase.ExternalDependencyFailure => 1.18,
            OptimizationLoadCase.HealthDegraded => 1.15,
            OptimizationLoadCase.RestartRecovery => 1.10,
            _ => 1.0
        };
        var policy = kind switch
        {
            OptimizationPolicyKind.CurrentProductionBaseline => new PolicyProjection(1.00, 1.00, 1.00, 1.00, 1.00, 1.00),
            OptimizationPolicyKind.ShortestDistance => new PolicyProjection(1.08, 0.88, 0.94, 0.90, 1.05, 1.02),
            OptimizationPolicyKind.HealthAware => new PolicyProjection(0.98, 1.04, 1.02, 1.02, 0.78, 0.70),
            OptimizationPolicyKind.EnergyAware => new PolicyProjection(0.96, 1.08, 1.04, 0.72, 0.92, 0.90),
            OptimizationPolicyKind.SlaAware => new PolicyProjection(1.05, 0.92, 0.82, 1.04, 1.02, 0.94),
            OptimizationPolicyKind.BalancedMultiObjective => new PolicyProjection(1.04, 0.94, 0.90, 0.84, 0.84, 0.82),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var durationSeconds = Math.Max(1d, profile.VirtualDurationMilliseconds / 1000d);
        var throughput = profile.MissionCount / durationSeconds * 60d * policy.Throughput / loadFactor;
        var meanLead = 8d * loadFactor * policy.LeadTime;
        var p95Lead = meanLead * 1.30;
        var meanWait = Math.Max(0.25d, profile.ConcurrentMissions * 0.75d * loadFactor * policy.WaitTime);
        var p95Wait = meanWait * 1.50;
        var distanceMeters = profile.MissionCount * profile.SegmentsPerMission * 1.0d;
        var energy = distanceMeters * loadFactor * policy.Energy;
        var wear = distanceMeters * loadFactor * policy.Wear;
        var riskLoad = loadCase == OptimizationLoadCase.HealthDegraded ? 1.45 : 1d;
        var failureRisk = profile.MissionCount * 0.10d * loadFactor * riskLoad * policy.FailureRisk;
        var slaViolations = (int)Math.Round(Math.Max(0d,
            (loadCase is OptimizationLoadCase.PeakLoad or OptimizationLoadCase.SegmentBlocked ? 2d : 0.5d) * policy.WaitTime));
        var recovery = loadCase is OptimizationLoadCase.RestartRecovery or OptimizationLoadCase.ExternalDependencyFailure
            ? 12d * loadFactor * policy.LeadTime
            : 2d * loadFactor;

        return new OptimizationMetrics
        {
            Throughput = Round(throughput),
            MeanMissionLeadTimeSeconds = Round(meanLead),
            P95MissionLeadTimeSeconds = Round(p95Lead),
            MeanWaitTimeSeconds = Round(meanWait),
            P95WaitTimeSeconds = Round(p95Wait),
            DeadlockCount = report.Profile.ConservationSatisfied ? 0 : 1,
            ConflictCount = loadCase == OptimizationLoadCase.SegmentBlocked ? 1 : 0,
            EnergyEstimate = Round(energy),
            WearIndex = Round(wear),
            FailureRiskExposure = Round(failureRisk),
            SlaViolationCount = slaViolations,
            RecoveryTimeSeconds = Round(recovery)
        };
    }

    private static IReadOnlyList<OptimizationStageEvidence> BuildStageEvidence(
        OptimizationExperimentDefinition definition,
        OptimizationPolicyCandidate policy,
        OptimizationLoadCase loadCase,
        int seed,
        int round,
        CapacityRunReport report,
        bool hardConstraintsSatisfied)
    {
        var stageDetails = new Dictionary<OptimizationSimulationStage, string>
        {
            [OptimizationSimulationStage.S0Governance] = $"definition={definition.DefinitionHash};scenario={definition.ScenarioEvidenceHash};constraint={definition.ConstraintProfileHash}",
            [OptimizationSimulationStage.S1ScenarioEngine] = $"stateHash={report.Profile.FinalStateHash};deterministicSeed={seed}",
            [OptimizationSimulationStage.S2VirtualPlc] = "executed-through-s7-capacity-profile;virtual-only=true",
            [OptimizationSimulationStage.S3VirtualRgv] = "executed-through-s7-capacity-profile;virtual-only=true",
            [OptimizationSimulationStage.S4VirtualTraffic] = $"executed-through-s7-capacity-profile;conservation={report.Profile.ConservationSatisfied}",
            [OptimizationSimulationStage.S5VirtualExternal] = "executed-through-s7-capacity-profile;networkClient=false",
            [OptimizationSimulationStage.S6VirtualHealth] = "executed-through-s7-capacity-profile;productionModel=false",
            [OptimizationSimulationStage.S7IntegratedRecovery] = $"missionCount={report.Profile.MissionCount};state={report.Profile.State}",
            [OptimizationSimulationStage.S8CapacityReadiness] = $"admission={report.Admission.Accepted};bounded={report.Profile.BoundedStateSatisfied};evidence={report.Profile.EvidenceHash}",
            [OptimizationSimulationStage.S9HilSoftwareBoundary] = "software-side-boundary-only;realHilExecuted=false;siteAccepted=false",
            [OptimizationSimulationStage.S10UnifiedVerification] = "read-only-catalog-boundary;remoteControlAllowed=false"
        };

        return OptimizationGovernance.RequiredSimulationStages.Select(stage =>
        {
            var s9 = stage == OptimizationSimulationStage.S9HilSoftwareBoundary;
            var detail = stageDetails[stage];
            return new OptimizationStageEvidence
            {
                Stage = stage,
                Available = !s9,
                Executed = stage is >= OptimizationSimulationStage.S1ScenarioEngine and <= OptimizationSimulationStage.S8CapacityReadiness,
                ReadOnlyBoundary = stage is OptimizationSimulationStage.S0Governance or OptimizationSimulationStage.S9HilSoftwareBoundary or OptimizationSimulationStage.S10UnifiedVerification,
                RequiresRealHardware = s9,
                RealHardwareExecuted = false,
                HardConstraintsSatisfied = hardConstraintsSatisfied,
                EvidenceHash = OptimizationHash.ComputeStageEvidenceHash(stage, definition.DefinitionHash, policy.PolicyHash, loadCase, seed, round, detail),
                Detail = detail
            };
        }).ToArray();
    }

    private static string BuildScenarioHash(OptimizationExperimentDefinition definition, OptimizationLoadCase loadCase, int seed) =>
        Hash(string.Join("\n",
            definition.ScenarioEvidenceHash,
            definition.TopologyEvidenceHash,
            definition.OrderDatasetEvidenceHash,
            definition.ConstraintProfileHash,
            ((int)loadCase).ToString(),
            seed.ToString()));

    private static CapacityReadinessRuntime CapacityRuntime(SimulationStateStore state) =>
        new(state, EngineOptions(), CapacityOptions(), IntegrationOptions(), PlcOptions(), RgvOptions(), TrafficOptions(), ExternalOptions(), HealthOptions());

    private static SimulationScenarioEngineOptions EngineOptions() => new()
    {
        MaximumStateEntries = 50_000,
        MaximumStateValueCharacters = 16_384,
        MaximumCheckpointBytes = 64 * 1024 * 1024,
        MaximumTimelineItems = 500_000,
        MaximumSpeedFactor = 10_000
    };

    private static CapacityReadinessOptions CapacityOptions() => new()
    {
        MaximumMissionsPerProfile = 64,
        MaximumConcurrentMissions = 16,
        MaximumSegmentsPerMission = 8,
        MaximumSamplesPerProfile = 128,
        MaximumProfiles = 16,
        MaximumWallClockMilliseconds = 120_000,
        MaximumRssGrowthBytes = 268_435_456
    };

    private static VirtualIntegrationOptions IntegrationOptions() => new()
    {
        MaximumMissions = 128,
        MaximumSegmentsPerMission = 16,
        MaximumAuditRecords = 20_000,
        ReservationLeaseMilliseconds = 60_000,
        ExternalAckMaximumAttempts = 3,
        ExternalAckTimeoutMilliseconds = 5_000,
        ExternalAckRetryDelayMilliseconds = 1_000
    };

    private static VirtualPlcOptions PlcOptions() => new()
    {
        MaximumBlocks = 128,
        MaximumBlockBytes = 65_536,
        MaximumOperationBytes = 65_536,
        MaximumScenarioTransferBytes = 1_536,
        MaximumFaults = 1_024,
        MaximumFaultPayloadBytes = 1_536,
        MaximumAuditRecords = 20_000
    };

    private static VirtualRgvOptions RgvOptions() => new()
    {
        MaximumVehicles = 128,
        MaximumSegments = 2_048,
        MaximumRouteSegments = 64,
        MaximumAuditRecords = 20_000
    };

    private static VirtualTrafficOptions TrafficOptions() => new()
    {
        MaximumZones = 2_048,
        MaximumSegmentsPerZone = 16,
        MaximumReservations = 4_096,
        MaximumWaitingRequests = 4_096,
        MaximumDeadlocks = 1_024,
        MaximumAuditRecords = 20_000,
        MaximumRollingLookAheadSegments = 64,
        DefaultReservationLeaseMilliseconds = 60_000,
        MaximumReservationLeaseMilliseconds = 604_800_000
    };

    private static VirtualExternalOptions ExternalOptions() => new()
    {
        MaximumEndpoints = 128,
        MaximumFaults = 1_024,
        MaximumRequests = 2_048,
        MaximumAuditRecords = 20_000,
        MaximumRetryAttempts = 8,
        DefaultTimeoutMilliseconds = 5_000,
        MaximumDelayMilliseconds = 604_800_000,
        CircuitFailureThreshold = 5,
        CircuitOpenMilliseconds = 30_000
    };

    private static VirtualHealthOptions HealthOptions() => new()
    {
        MaximumAssets = 128,
        MaximumSamplesPerAsset = 1_000,
        MaximumForecastsPerAsset = 128,
        MaximumOutcomesPerAsset = 64,
        MaximumGeneratedSamplesPerAction = 1_000,
        MaximumAuditRecords = 20_000,
        ForecastMinimumHistoryPoints = 48,
        ForecastMinimumHistorySpanHours = 24,
        ForecastMaximumHistoryPoints = 1_000,
        TrendWindowSize = 48,
        TrendChangeThreshold = 2,
        HealthyMinimumScore = 85,
        AttentionMinimumScore = 70,
        DegradedMinimumScore = 40,
        MaximumRulHours = 17_520
    };

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PolicyProjection(
        double Throughput,
        double LeadTime,
        double WaitTime,
        double Energy,
        double Wear,
        double FailureRisk);
}
