namespace Wcs.Simulator.VirtualIntegration;

using Wcs.Simulator.VirtualExternal;

public sealed class VirtualIntegrationOptions
{
    public const string SectionName = "SimulationVirtualIntegration";

    public int MaximumMissions { get; set; } = 256;
    public int MaximumSegmentsPerMission { get; set; } = 32;
    public int MaximumAuditRecords { get; set; } = 10_000;
    public long ReservationLeaseMilliseconds { get; set; } = 60_000;
    public int ExternalAckMaximumAttempts { get; set; } = 3;
    public long ExternalAckTimeoutMilliseconds { get; set; } = 5_000;
    public long ExternalAckRetryDelayMilliseconds { get; set; } = 1_000;

    public void Validate()
    {
        if (MaximumMissions is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.MaximumMissions must be between 1 and 100,000.");
        if (MaximumSegmentsPerMission is < 1 or > 1_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.MaximumSegmentsPerMission must be between 1 and 1,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (ReservationLeaseMilliseconds is < 1 or > 31_536_000_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.ReservationLeaseMilliseconds must be between 1 millisecond and 365 days.");
        if (ExternalAckMaximumAttempts is < 1 or > 100)
            throw new InvalidOperationException("SimulationVirtualIntegration.ExternalAckMaximumAttempts must be between 1 and 100.");
        if (ExternalAckTimeoutMilliseconds is < 1 or > 31_536_000_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.ExternalAckTimeoutMilliseconds must be between 1 millisecond and 365 days.");
        if (ExternalAckRetryDelayMilliseconds is < 0 or > 31_536_000_000)
            throw new InvalidOperationException("SimulationVirtualIntegration.ExternalAckRetryDelayMilliseconds must be between 0 and 365 days.");
    }
}

public enum VirtualIntegrationMissionState
{
    Defined = 0,
    Dispatched = 1,
    Moving = 2,
    Completed = 3,
    Acknowledged = 4
}

public sealed record VirtualIntegrationSegmentDefinition(
    string SegmentId,
    string FromNodeId,
    string ToNodeId,
    int LengthMillimeters,
    int SpeedLimitMillimetersPerSecond);

public sealed record VirtualIntegrationMissionDefinition
{
    public string MissionId { get; init; } = string.Empty;
    public string PlcBlockKey { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string LoadId { get; init; } = string.Empty;
    public string SourceNodeId { get; init; } = string.Empty;
    public string DestinationNodeId { get; init; } = string.Empty;
    public string ExternalEndpointId { get; init; } = string.Empty;
    public VirtualExternalSystemKind ExternalSystemKind { get; init; } = VirtualExternalSystemKind.Mes;
    public string HealthAssetId { get; init; } = string.Empty;
    public int Priority { get; init; } = 100;
    public int VehicleSpeedMillimetersPerSecond { get; init; } = 1_000;
    public int VehicleBatteryPercent { get; init; } = 100;
    public double InitialHealthScore { get; init; } = 95;
    public double InitialFusionRiskScore { get; init; } = 0.05;
    public IReadOnlyList<VirtualIntegrationSegmentDefinition> Segments { get; init; } = [];
}

public sealed record VirtualIntegrationMissionSnapshot(
    string MissionId,
    VirtualIntegrationMissionState State,
    string PlcBlockKey,
    string VehicleId,
    string LoadId,
    string SourceNodeId,
    string DestinationNodeId,
    string ExternalEndpointId,
    string HealthAssetId,
    IReadOnlyList<string> SegmentIds,
    int Priority,
    long DefinedAtOffsetMilliseconds,
    long LastUpdatedOffsetMilliseconds,
    long Version);

public sealed record VirtualIntegrationConsistencySnapshot(
    string MissionId,
    VirtualIntegrationMissionState State,
    bool VehicleAtDestination,
    bool VehicleUnloaded,
    bool TrafficClean,
    bool ExternalExactlyOnce,
    bool ExternalSucceeded,
    bool PlcFlagsConsistent,
    bool HealthOutcomeExactlyOnce,
    bool NoActiveDeadlock,
    bool IsConsistent,
    string Detail);

public sealed record VirtualIntegrationAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string MissionId,
    string? Detail,
    bool Success);

public sealed record VirtualIntegrationStatus(
    int MissionCount,
    int DefinedCount,
    int ActiveCount,
    int CompletedCount,
    int AcknowledgedCount,
    int AuditCount,
    long OperationSequence);
