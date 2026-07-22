namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 交通互斥资源类型。多个存在冲突关系的路段可以映射为同一个资源。
/// </summary>
public enum TransportTrafficResourceKind
{
    BlockSection = 0,
    Intersection = 1,
    SingleTrack = 2,
    MergePoint = 3,
    Custom = 4
}

public sealed record TransportTrafficResourceDefinition
{
    public string ResourceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public TransportTrafficResourceKind Kind { get; init; }
    public IReadOnlyList<string> EdgeIds { get; init; } = Array.Empty<string>();
    public int Capacity { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public int AgingIntervalSeconds { get; init; } = 30;
}

/// <summary>
/// 交通请求元数据。OwnerId 通常等于运输 RequestId。
/// </summary>
public sealed record TransportTrafficRequestInfo
{
    public string OwnerId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public int Priority { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportTrafficHold
{
    public string ResourceId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public bool OccupancyConfirmed { get; init; }
    public DateTime AcquiredAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed record TransportTrafficWait
{
    public string WaitId { get; init; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public IReadOnlyList<string> RequestedResourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingOwnerIds { get; init; } = Array.Empty<string>();
    public int Priority { get; init; }
    public DateTime WaitingSinceUtc { get; init; } = DateTime.UtcNow;
    public string Reason { get; init; } = string.Empty;
}

public sealed record TransportTrafficAcquireResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingOwnerIds { get; init; } = Array.Empty<string>();
    public string? FailureReason { get; init; }

    public static TransportTrafficAcquireResult Granted(IReadOnlyList<string> resources) =>
        new() { Success = true, ResourceIds = resources };

    public static TransportTrafficAcquireResult Denied(
        IReadOnlyList<string> resources,
        IReadOnlyList<string> blockers,
        string reason) =>
        new()
        {
            Success = false,
            ResourceIds = resources,
            BlockingOwnerIds = blockers,
            FailureReason = reason
        };
}

public sealed record TransportDeadlockCycle
{
    public string CycleId { get; init; } = string.Empty;
    public IReadOnlyList<string> OwnerIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum TransportDeadlockResolutionStatus
{
    Resolved = 0,
    CycleBrokenAwaitingClearance = 1,
    RequiresManualIntervention = 2,
    CycleNotFound = 3
}

public sealed record TransportDeadlockResolution
{
    public string CycleId { get; init; } = string.Empty;
    public string? VictimOwnerId { get; init; }
    public TransportDeadlockResolutionStatus Status { get; init; }
    public IReadOnlyList<string> ReleasedResourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RetainedOccupiedResourceIds { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = string.Empty;
    public DateTime ResolvedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportTrafficIncident
{
    public string IncidentId { get; init; } = Guid.NewGuid().ToString("N");
    public string IncidentType { get; init; } = string.Empty;
    public string? OwnerId { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportTrafficSnapshot
{
    public IReadOnlyList<TransportTrafficResourceDefinition> Resources { get; init; } = Array.Empty<TransportTrafficResourceDefinition>();
    public IReadOnlyList<TransportTrafficRequestInfo> Requests { get; init; } = Array.Empty<TransportTrafficRequestInfo>();
    public IReadOnlyList<TransportTrafficHold> Holds { get; init; } = Array.Empty<TransportTrafficHold>();
    public IReadOnlyList<TransportTrafficWait> Waits { get; init; } = Array.Empty<TransportTrafficWait>();
    public IReadOnlyList<TransportTrafficIncident> Incidents { get; init; } = Array.Empty<TransportTrafficIncident>();
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}
