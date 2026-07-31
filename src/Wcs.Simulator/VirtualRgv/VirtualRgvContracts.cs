namespace Wcs.Simulator.VirtualRgv;

using Wcs.Core.TransportScheduling;

public sealed class VirtualRgvOptions
{
    public const string SectionName = "SimulationVirtualRgv";

    public int MaximumVehicles { get; set; } = 256;
    public int MaximumSegments { get; set; } = 2_048;
    public int MaximumRouteSegments { get; set; } = 256;
    public int MaximumAuditRecords { get; set; } = 5_000;
    public int MaximumSegmentLengthMillimeters { get; set; } = 10_000_000;
    public int MaximumSpeedMillimetersPerSecond { get; set; } = 20_000;
    public int BatteryDrainBasisPointsPerMeter { get; set; } = 1;

    public void Validate()
    {
        if (MaximumVehicles is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumVehicles must be between 1 and 100,000.");
        if (MaximumSegments is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumSegments must be between 1 and 1,000,000.");
        if (MaximumRouteSegments is < 1 or > 10_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumRouteSegments must be between 1 and 10,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (MaximumSegmentLengthMillimeters is < 1 or > 1_000_000_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumSegmentLengthMillimeters must be between 1 and 1,000,000,000.");
        if (MaximumSpeedMillimetersPerSecond is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualRgv.MaximumSpeedMillimetersPerSecond must be between 1 and 1,000,000.");
        if (BatteryDrainBasisPointsPerMeter is < 0 or > 10_000)
            throw new InvalidOperationException("SimulationVirtualRgv.BatteryDrainBasisPointsPerMeter must be between 0 and 10,000.");
    }
}

public sealed record VirtualRgvSegmentDefinition
{
    public string SegmentId { get; init; } = string.Empty;
    public string FromNodeId { get; init; } = string.Empty;
    public string ToNodeId { get; init; } = string.Empty;
    public int LengthMillimeters { get; init; }
    public int SpeedLimitMillimetersPerSecond { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record VirtualRgvVehicleDefinition
{
    public string VehicleId { get; init; } = string.Empty;
    public string InitialNodeId { get; init; } = string.Empty;
    public int SpeedMillimetersPerSecond { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public bool IsOnline { get; init; } = true;
    public string? LoadId { get; init; }
    public TransportVehicleCapability Capabilities { get; init; } = TransportVehicleCapability.Carry;
}

public sealed record VirtualRgvSegmentSnapshot(
    string SegmentId,
    string FromNodeId,
    string ToNodeId,
    int LengthMillimeters,
    int SpeedLimitMillimetersPerSecond,
    bool Enabled);

public sealed record VirtualRgvVehicleSnapshot(
    string VehicleId,
    TransportVehicleOperatingState State,
    bool IsOnline,
    string? CurrentNodeId,
    string? CurrentSegmentId,
    int SegmentProgressMillimeters,
    long SegmentElapsedMilliseconds,
    IReadOnlyList<string> RouteSegmentIds,
    int RouteIndex,
    int SpeedMillimetersPerSecond,
    int BatteryBasisPoints,
    long BatteryDrainRemainder,
    string? LoadId,
    TransportVehicleCapability Capabilities,
    long Version,
    long LastUpdatedOffsetMilliseconds)
{
    public int BatteryPercent => Math.Clamp(BatteryBasisPoints / 100, 0, 100);
    public bool RouteCompleted => RouteSegmentIds.Count > 0 && RouteIndex >= RouteSegmentIds.Count;
    public bool IsAtNode => CurrentSegmentId is null && !string.IsNullOrWhiteSpace(CurrentNodeId);

    public TransportVehicleSnapshot ToTransportSnapshot(DateTimeOffset occurredAtUtc) => new()
    {
        VehicleId = VehicleId,
        Kind = TransportVehicleKind.Rgv,
        State = State,
        CurrentNodeId = CurrentNodeId ?? string.Empty,
        IsOnline = IsOnline,
        BatteryPercent = BatteryPercent,
        ActiveTaskCount = State == TransportVehicleOperatingState.Executing ? 1 : 0,
        Capabilities = Capabilities,
        Version = Version,
        UpdatedAtUtc = occurredAtUtc.UtcDateTime
    };
}

public sealed record VirtualRgvAdvanceResult(
    string VehicleId,
    long FromOffsetMilliseconds,
    long ToOffsetMilliseconds,
    long DistanceMovedMillimeters,
    IReadOnlyList<string> CompletedSegmentIds,
    VirtualRgvVehicleSnapshot Vehicle);

public sealed record VirtualRgvSegmentOccupancy(
    string SegmentId,
    IReadOnlyList<string> VehicleIds);

public sealed record VirtualRgvAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string Target,
    string? Detail,
    bool Success);

public sealed record VirtualRgvStatus(
    int VehicleCount,
    int SegmentCount,
    int ExecutingVehicleCount,
    int OccupiedSegmentCount,
    int AuditCount,
    long OperationSequence);
