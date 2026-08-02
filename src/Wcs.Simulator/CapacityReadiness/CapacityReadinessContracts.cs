namespace Wcs.Simulator.CapacityReadiness;

/// <summary>
/// Simulation-only S8 capacity and HIL-readiness configuration. 8h/24h are virtual-time
/// profiles and never imply real HIL, mechanical safety, network/protocol or site acceptance.
/// </summary>
public sealed class CapacityReadinessOptions
{
    public const string SectionName = "SimulationCapacityReadiness";

    public int MaximumProfiles { get; set; } = 64;
    public int MaximumMissionsPerProfile { get; set; } = 256;
    public int MaximumConcurrentMissions { get; set; } = 64;
    public int MaximumSegmentsPerMission { get; set; } = 8;
    public int MaximumSamplesPerProfile { get; set; } = 2_000;
    public int MaximumAuditRecords { get; set; } = 10_000;
    public long EightHourVirtualDurationMilliseconds { get; set; } = 28_800_000;
    public long TwentyFourHourVirtualDurationMilliseconds { get; set; } = 86_400_000;
    public long MaximumWallClockMilliseconds { get; set; } = 120_000;
    public long MaximumRssGrowthBytes { get; set; } = 268_435_456;

    public void Validate()
    {
        if (MaximumProfiles is < 1 or > 10_000) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumProfiles must be between 1 and 10,000.");
        if (MaximumMissionsPerProfile is < 1 or > 100_000) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumMissionsPerProfile must be between 1 and 100,000.");
        if (MaximumConcurrentMissions is < 1 or > 100_000 || MaximumConcurrentMissions > MaximumMissionsPerProfile) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumConcurrentMissions must be between 1 and MaximumMissionsPerProfile.");
        if (MaximumSegmentsPerMission is < 1 or > 1_000) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumSegmentsPerMission must be between 1 and 1,000.");
        if (MaximumSamplesPerProfile is < 1 or > 1_000_000) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumSamplesPerProfile must be between 1 and 1,000,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000) throw new InvalidOperationException("SimulationCapacityReadiness.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (EightHourVirtualDurationMilliseconds != 28_800_000) throw new InvalidOperationException("EightHourVirtualDurationMilliseconds must remain exactly 8 virtual hours.");
        if (TwentyFourHourVirtualDurationMilliseconds != 86_400_000) throw new InvalidOperationException("TwentyFourHourVirtualDurationMilliseconds must remain exactly 24 virtual hours.");
        if (MaximumWallClockMilliseconds is < 1_000 or > 3_600_000) throw new InvalidOperationException("MaximumWallClockMilliseconds must be between 1 second and 1 hour.");
        if (MaximumRssGrowthBytes is < 16_777_216 or > 2_147_483_648L) throw new InvalidOperationException("MaximumRssGrowthBytes must be between 16 MB and 2 GB.");
    }
}

public enum CapacityProfileKind { Nominal, Peak, EightHourVirtualSoak, TwentyFourHourVirtualSoak }
public enum CapacityProfileState { Defined, Running, Completed, Rejected }

public sealed record CapacityProfileDefinition(
    string ProfileId,
    CapacityProfileKind Kind,
    int MissionCount,
    int ConcurrentMissions,
    int SegmentsPerMission,
    long VirtualDurationMilliseconds);

public sealed record CapacityAdmissionResult(bool Accepted, IReadOnlyList<string> Violations, long EstimatedStateEntries);

public sealed record CapacitySample(
    long Sequence,
    long VirtualOffsetMilliseconds,
    int DefinedMissions,
    int AcknowledgedMissions,
    long StateEntryCount,
    string StateHash);

public sealed record CapacityProfileSnapshot(
    string ProfileId,
    CapacityProfileKind Kind,
    CapacityProfileState State,
    int MissionCount,
    int ConcurrentMissions,
    int SegmentsPerMission,
    long VirtualDurationMilliseconds,
    int SampleCount,
    bool ConservationSatisfied,
    bool BoundedStateSatisfied,
    string FinalStateHash,
    string? Detail);

public sealed record CapacityRunReport(
    CapacityProfileSnapshot Profile,
    CapacityAdmissionResult Admission,
    IReadOnlyList<CapacitySample> Samples,
    long ElapsedMilliseconds,
    long RssBeforeBytes,
    long RssAfterBytes,
    long RssGrowthBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int HandleCount,
    bool ResourceBudgetSatisfied);

/// <summary>Software-side permission to start S9 planning; this is deliberately not a real-HIL pass.</summary>
public sealed record HilReadinessSnapshot(
    bool SimulationIsolationVerified,
    bool ProductionFailClosedVerified,
    bool DeterministicReplayVerified,
    bool CheckpointRestoreVerified,
    bool CapacityBoundaryVerified,
    bool EightHourVirtualSoakVerified,
    bool TwentyFourHourVirtualSoakVerified,
    bool StateAndQueueConservationVerified,
    bool NoProductionControlWritesVerified,
    bool RealHilExecuted,
    bool MechanicalSafetyAccepted,
    bool SiteAccepted,
    bool ReadyToEnterS9,
    IReadOnlyList<string> MissingExternalPrerequisites);
