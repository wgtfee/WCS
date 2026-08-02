namespace Wcs.Simulator.CapacityReadiness;

/// <summary>
/// Simulation-only S8 capacity and HIL-readiness configuration.
/// The 8h/24h durations are virtual-time profiles; they are not claims of
/// wall-clock HIL endurance or site acceptance.
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
    public long SampleIntervalMilliseconds { get; set; } = 60_000;
    public long EightHourVirtualDurationMilliseconds { get; set; } = 28_800_000;
    public long TwentyFourHourVirtualDurationMilliseconds { get; set; } = 86_400_000;

    public void Validate()
    {
        if (MaximumProfiles is < 1 or > 10_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumProfiles must be between 1 and 10,000.");
        if (MaximumMissionsPerProfile is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumMissionsPerProfile must be between 1 and 100,000.");
        if (MaximumConcurrentMissions is < 1 or > 100_000 || MaximumConcurrentMissions > MaximumMissionsPerProfile)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumConcurrentMissions must be between 1 and MaximumMissionsPerProfile.");
        if (MaximumSegmentsPerMission is < 1 or > 1_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumSegmentsPerMission must be between 1 and 1,000.");
        if (MaximumSamplesPerProfile is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumSamplesPerProfile must be between 1 and 1,000,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (SampleIntervalMilliseconds is < 1 or > 86_400_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.SampleIntervalMilliseconds must be between 1 millisecond and 24 hours.");
        if (EightHourVirtualDurationMilliseconds != 28_800_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.EightHourVirtualDurationMilliseconds must remain exactly 8 virtual hours.");
        if (TwentyFourHourVirtualDurationMilliseconds != 86_400_000)
            throw new InvalidOperationException("SimulationCapacityReadiness.TwentyFourHourVirtualDurationMilliseconds must remain exactly 24 virtual hours.");
        if (SampleIntervalMilliseconds > EightHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("SimulationCapacityReadiness.SampleIntervalMilliseconds cannot exceed the 8-hour virtual profile.");
    }
}

public enum CapacityProfileKind
{
    Nominal = 0,
    Peak = 1,
    Saturation = 2,
    EightHourVirtualSoak = 3,
    TwentyFourHourVirtualSoak = 4
}

public enum CapacityProfileState
{
    Defined = 0,
    Running = 1,
    Completed = 2,
    Rejected = 3
}

public sealed record CapacityProfileDefinition(
    string ProfileId,
    CapacityProfileKind Kind,
    int MissionCount,
    int ConcurrentMissions,
    int SegmentsPerMission,
    long VirtualDurationMilliseconds,
    long MissionSpacingMilliseconds);

public sealed record CapacitySample(
    long Sequence,
    long VirtualOffsetMilliseconds,
    int DefinedMissions,
    int ActiveMissions,
    int AcknowledgedMissions,
    int ActiveReservations,
    int WaitingRequests,
    int ActiveDeadlocks,
    int ExternalRequests,
    int HealthOutcomes,
    long StateEntryCount);

public sealed record CapacityProfileSnapshot(
    string ProfileId,
    CapacityProfileKind Kind,
    CapacityProfileState State,
    int MissionCount,
    int ConcurrentMissions,
    int SegmentsPerMission,
    long VirtualDurationMilliseconds,
    long MissionSpacingMilliseconds,
    int SampleCount,
    int PeakActiveMissions,
    int PeakReservations,
    int PeakWaitingRequests,
    int PeakDeadlocks,
    bool ConservationSatisfied,
    bool BoundedStateSatisfied,
    string? Detail);

/// <summary>
/// Repository-level readiness for entering S9 HIL work. A passing S8 gate only
/// proves that the software-side contracts and evidence prerequisites exist.
/// It never means real HIL, mechanical safety, protocol, network or site acceptance passed.
/// </summary>
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

public sealed record CapacityAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string ProfileId,
    string? Detail,
    bool Success);
