namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 第二阶段运输任务执行状态。
/// </summary>
public enum TransportExecutionState
{
    Assigned = 0,
    MovingToPickup = 1,
    Loading = 2,
    MovingToDestination = 3,
    Unloading = 4,
    WaitingForRoute = 5,
    Paused = 6,
    Faulted = 7,
    Completed = 8,
    Cancelled = 9
}

public enum TransportExecutionCommandType
{
    MoveToNode = 0,
    Load = 1,
    Unload = 2,
    Stop = 3
}

/// <summary>
/// EMS/RGV 控制器或 PLC 上报的位置反馈。
/// Sequence 必须单调递增，用于拒绝乱序和重复报文。
/// </summary>
public sealed record TransportPositionFeedback
{
    public string VehicleId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 执行层生成的逻辑命令。协议适配器负责把它映射为实际 PLC/EMS/RGV 指令。
/// </summary>
public sealed record TransportExecutionCommand
{
    public string CommandId { get; init; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportExecutionCommandType CommandType { get; init; }
    public string? TargetNodeId { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 可供 API、Desktop 和恢复模块读取的执行快照。
/// </summary>
public sealed record TransportExecutionSnapshot
{
    public string RequestId { get; init; } = string.Empty;
    public string AssignmentId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string? LoadId { get; init; }
    public TransportExecutionState State { get; init; } = TransportExecutionState.Assigned;
    public string CurrentNodeId { get; init; } = string.Empty;
    public int CurrentNodeIndex { get; init; }
    public int PickupNodeIndex { get; init; }
    public long LastFeedbackSequence { get; init; } = -1;
    public IReadOnlyList<string> FullNodePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FullEdgePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActiveReservedEdges { get; init; } = Array.Empty<string>();
    public string ReservationId { get; init; } = string.Empty;
    public TimeSpan ReservationLease { get; init; } = TimeSpan.FromSeconds(30);
    public int ReservationWindowEdges { get; init; } = 2;
    public string? LastError { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsTerminal =>
        State is TransportExecutionState.Completed or TransportExecutionState.Cancelled;
}

public sealed record TransportExecutionResult
{
    public bool Success { get; init; }
    public TransportExecutionSnapshot? Snapshot { get; init; }
    public string? FailureReason { get; init; }

    public static TransportExecutionResult Succeeded(TransportExecutionSnapshot snapshot) =>
        new() { Success = true, Snapshot = snapshot };

    public static TransportExecutionResult Failed(string reason, TransportExecutionSnapshot? snapshot = null) =>
        new() { Success = false, Snapshot = snapshot, FailureReason = reason };
}
