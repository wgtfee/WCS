namespace Wcs.Core.TransportScheduling;

using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;

/// <summary>
/// 统一运输车辆类型。第一阶段覆盖 EMS 空中台车与地面 RGV。
/// </summary>
public enum TransportVehicleKind
{
    Ems = 0,
    Rgv = 1
}

/// <summary>
/// 车辆运行状态。
/// </summary>
public enum TransportVehicleOperatingState
{
    Offline = 0,
    Idle = 1,
    Executing = 2,
    Charging = 3,
    Faulted = 4,
    Maintenance = 5,
    ChargingRequested = 6,
    WaitingForCharge = 7
}

/// <summary>
/// 车辆能力标签。后续可继续扩展载重、夹具和工艺能力。
/// </summary>
[Flags]
public enum TransportVehicleCapability
{
    None = 0,
    Carry = 1 << 0,
    Lift = 1 << 1,
    Transfer = 1 << 2,
    All = Carry | Lift | Transfer
}

/// <summary>
/// 车辆状态快照。由 EMS/RGV 协议适配器写入，调度层只消费统一模型。
/// </summary>
public sealed record TransportVehicleSnapshot
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public TransportVehicleOperatingState State { get; init; } = TransportVehicleOperatingState.Offline;
    public string CurrentNodeId { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public int ActiveTaskCount { get; init; }
    public TransportVehicleCapability Capabilities { get; init; } = TransportVehicleCapability.Carry;
    public long Version { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public bool CanAcceptTask =>
        IsOnline &&
        State == TransportVehicleOperatingState.Idle &&
        !string.IsNullOrWhiteSpace(CurrentNodeId);
}

/// <summary>
/// EMS/RGV 统一派单请求。
/// </summary>
public sealed record TransportDispatchRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string SourceNodeId { get; init; } = string.Empty;
    public string DestinationNodeId { get; init; } = string.Empty;
    public string? LoadId { get; init; }
    public int Priority { get; init; }
    public TransportVehicleCapability RequiredCapability { get; init; } = TransportVehicleCapability.Carry;
    public EdgeCapability? RequiredEdgeCapability { get; init; }
    public IReadOnlySet<TransportVehicleKind>? AllowedVehicleKinds { get; init; }
    public TransportRouteStrategy RouteStrategy { get; init; } = TransportRouteStrategy.Balanced;
    public TimeSpan ReservationLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 第二阶段滚动预留窗口。派单时只预留车辆前方若干条边，位置推进后动态释放和补充。
    /// </summary>
    public int ReservationWindowEdges { get; init; } = 2;

    /// <summary>
    /// 第五阶段最低派单电量。低于该值的空闲车辆退出普通派单候选集。
    /// </summary>
    public int MinimumBatteryPercent { get; init; } = 20;

    /// <summary>
    /// 紧急任务可显式绕过最低电量保护。默认禁止。
    /// </summary>
    public bool AllowLowBatteryOverride { get; init; }

    /// <summary>
    /// 指定唯一车辆，用于恢复、测试或受控的专车任务。
    /// </summary>
    public string? RequiredVehicleId { get; init; }
}

/// <summary>
/// 候选车辆及其到取货点的空驶路径评分。
/// </summary>
public sealed record TransportVehicleCandidate
{
    public TransportVehicleSnapshot Vehicle { get; init; } = new();
    public TransportRouteResult PickupRoute { get; init; } = TransportRouteResult.NotFound();
    public int Score { get; init; }
}

/// <summary>
/// 一次原子路段预留。
/// </summary>
public sealed record RouteReservation
{
    public string ReservationId { get; init; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; init; } = string.Empty;
    public IReadOnlyList<string> EdgeIds { get; init; } = Array.Empty<string>();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; }
}

/// <summary>
/// 调度成功后生成的不可变分配结果。
/// </summary>
public sealed record TransportDispatchAssignment
{
    public string AssignmentId { get; init; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind VehicleKind { get; init; }
    public string? LoadId { get; init; }
    public IReadOnlyList<string> PickupNodePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PickupEdgePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LoadedNodePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LoadedEdgePath { get; init; } = Array.Empty<string>();
    public string ReservationId { get; init; } = string.Empty;
    public TimeSpan ReservationLease { get; init; } = TimeSpan.FromSeconds(30);
    public int ReservationWindowEdges { get; init; } = 2;
    public int Priority { get; init; }
    public TransportVehicleCapability RequiredCapability { get; init; } = TransportVehicleCapability.Carry;
    public EdgeCapability? RequiredEdgeCapability { get; init; }
    public TransportRouteStrategy RouteStrategy { get; init; } = TransportRouteStrategy.Balanced;
    public int MinimumBatteryPercent { get; init; } = 20;
    public bool AllowLowBatteryOverride { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<string> FullNodePath =>
        PickupNodePath
            .Concat(LoadedNodePath.Skip(PickupNodePath.Count > 0 ? 1 : 0))
            .ToArray();

    public IReadOnlyList<string> FullEdgePath =>
        PickupEdgePath.Concat(LoadedEdgePath).ToArray();
}

/// <summary>
/// 统一调度调用结果。
/// </summary>
public sealed record TransportDispatchResult
{
    public bool Success { get; init; }
    public TransportDispatchAssignment? Assignment { get; init; }
    public string? FailureReason { get; init; }

    public static TransportDispatchResult Succeeded(TransportDispatchAssignment assignment) =>
        new() { Success = true, Assignment = assignment };

    public static TransportDispatchResult Failed(string reason) =>
        new() { Success = false, FailureReason = reason };
}
