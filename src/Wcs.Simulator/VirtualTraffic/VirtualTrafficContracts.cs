namespace Wcs.Simulator.VirtualTraffic;

public sealed class VirtualTrafficOptions
{
    public const string SectionName = "SimulationVirtualTraffic";

    public int MaximumZones { get; set; } = 256;
    public int MaximumSegmentsPerZone { get; set; } = 16;
    public int MaximumReservations { get; set; } = 2_048;
    public int MaximumWaitingRequests { get; set; } = 2_048;
    public int MaximumDeadlocks { get; set; } = 512;
    public int MaximumAuditRecords { get; set; } = 5_000;
    public int MaximumRollingLookAheadSegments { get; set; } = 16;
    public long DefaultReservationLeaseMilliseconds { get; set; } = 60_000;
    public long MaximumReservationLeaseMilliseconds { get; set; } = 86_400_000;

    public void Validate()
    {
        if (MaximumZones is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumZones must be between 1 and 100,000.");
        if (MaximumSegmentsPerZone is < 1 or > 16)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumSegmentsPerZone must be between 1 and 16 so zone and wait-graph state remains bounded.");
        if (MaximumReservations is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumReservations must be between 1 and 1,000,000.");
        if (MaximumWaitingRequests is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumWaitingRequests must be between 1 and 1,000,000.");
        if (MaximumDeadlocks is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumDeadlocks must be between 1 and 100,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (MaximumRollingLookAheadSegments is < 1 or > 10_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumRollingLookAheadSegments must be between 1 and 10,000.");
        if (DefaultReservationLeaseMilliseconds is < 1 ||
            DefaultReservationLeaseMilliseconds > MaximumReservationLeaseMilliseconds)
            throw new InvalidOperationException("SimulationVirtualTraffic.DefaultReservationLeaseMilliseconds is outside the configured lease limit.");
        if (MaximumReservationLeaseMilliseconds is < 1 or > 31_536_000_000)
            throw new InvalidOperationException("SimulationVirtualTraffic.MaximumReservationLeaseMilliseconds must be between 1 millisecond and 365 days.");
    }
}

public enum VirtualTrafficZoneKind
{
    SharedSegment,
    OpposingDirection,
    Merge,
    Intersection,
    Custom
}

public enum VirtualTrafficReservationState
{
    Granted,
    Released,
    Expired
}

public enum VirtualTrafficRequestState
{
    Waiting,
    Granted,
    Cancelled
}

public sealed record VirtualTrafficZoneDefinition
{
    private int _capacity = 1;

    public string ZoneId { get; init; } = string.Empty;
    public IReadOnlyList<string> SegmentIds { get; init; } = [];
    public int Capacity
    {
        get => _capacity;
        init
        {
            if (value is < 1 or > 16)
                throw new InvalidOperationException("Virtual traffic zone capacity must be between 1 and 16 so blocking-vehicle state remains bounded.");
            _capacity = value;
        }
    }
    public VirtualTrafficZoneKind Kind { get; init; } = VirtualTrafficZoneKind.SharedSegment;
}

public sealed record VirtualTrafficZoneSnapshot(
    string ZoneId,
    IReadOnlyList<string> SegmentIds,
    int Capacity,
    VirtualTrafficZoneKind Kind);

public sealed record VirtualTrafficReservationSnapshot(
    string ReservationId,
    string ZoneId,
    string SegmentId,
    string VehicleId,
    int Priority,
    VirtualTrafficReservationState State,
    long GrantedAtOffsetMilliseconds,
    long ExpiresAtOffsetMilliseconds,
    long Version);

public sealed record VirtualTrafficWaitingRequestSnapshot(
    string RequestId,
    string ZoneId,
    string SegmentId,
    string VehicleId,
    int Priority,
    VirtualTrafficRequestState State,
    IReadOnlyList<string> BlockingVehicleIds,
    long RequestedAtOffsetMilliseconds,
    long LeaseMilliseconds,
    long Sequence,
    long Version);

public sealed record VirtualTrafficReservationDecision(
    bool Granted,
    string ZoneId,
    string SegmentId,
    string VehicleId,
    VirtualTrafficReservationSnapshot? Reservation,
    VirtualTrafficWaitingRequestSnapshot? WaitingRequest,
    IReadOnlyList<string> BlockingVehicleIds);

public sealed record VirtualTrafficWaitEdge(
    string WaitingVehicleId,
    string BlockingVehicleId,
    string ZoneId,
    string SegmentId,
    string RequestId);

public sealed record VirtualTrafficDeadlockSnapshot(
    string DeadlockId,
    IReadOnlyList<string> VehicleIds,
    IReadOnlyList<VirtualTrafficWaitEdge> Edges,
    string VictimVehicleId,
    long DetectedAtOffsetMilliseconds,
    bool Resolved,
    long? ResolvedAtOffsetMilliseconds,
    long Version);

public sealed record VirtualTrafficResolutionResult(
    string DeadlockId,
    string VictimVehicleId,
    IReadOnlyList<string> ReleasedReservationIds,
    IReadOnlyList<string> CancelledRequestIds,
    IReadOnlyList<string> NewlyGrantedRequestIds,
    VirtualTrafficDeadlockSnapshot Deadlock);

public sealed record VirtualTrafficRollingReservationResult(
    string VehicleId,
    IReadOnlyList<VirtualTrafficReservationDecision> Decisions,
    bool AllGranted);

public sealed record VirtualTrafficAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string Target,
    string? Detail,
    bool Success);

public sealed record VirtualTrafficStatus(
    int ZoneCount,
    int ActiveReservationCount,
    int WaitingRequestCount,
    int WaitEdgeCount,
    int ActiveDeadlockCount,
    int AuditCount,
    long OperationSequence);
