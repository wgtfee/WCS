namespace Wcs.Core.EventBus.Events;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 设备状态变化事件
/// </summary>
public class DeviceStateChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    public string DeviceId { get; set; } = string.Empty;

    public DeviceStatusEnum OldStatus { get; set; }

    public DeviceStatusEnum NewStatus { get; set; }

    public DeviceState? DeviceState { get; set; }
}

/// <summary>
/// 任务状态变化事件
/// </summary>
public class TaskStateChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    public string TaskId { get; set; } = string.Empty;

    public TaskStatusEnum OldStatus { get; set; }

    public TaskStatusEnum NewStatus { get; set; }

    public TaskRuntime? TaskRuntime { get; set; }
}

/// <summary>
/// 报警产生事件
/// </summary>
public class AlarmRaisedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;

    public string AlarmId { get; set; } = string.Empty;

    public string AlarmCode { get; set; } = string.Empty;

    public AlarmLevelEnum Level { get; set; }

    public string Message { get; set; } = string.Empty;

    public AlarmState? AlarmState { get; set; }
}

/// <summary>
/// 报警恢复事件
/// </summary>
public class AlarmRecoveredEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    public string AlarmId { get; set; } = string.Empty;

    public string AlarmCode { get; set; } = string.Empty;

    public DateTime RecoverTime { get; set; }
}

/// <summary>
/// 任务创建事件
/// </summary>
public class TaskCreatedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;

    public string TaskId { get; set; } = string.Empty;

    public int TaskPriority { get; set; }

    public string RouteId { get; set; } = string.Empty;

    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// 任务完成事件
/// </summary>
public class TaskCompletedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    public string TaskId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }
}

/// <summary>
/// PLC 数据块变化事件
/// </summary>
public class PlcBlockChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;

    public string BlockName { get; set; } = string.Empty;

    public Dictionary<string, object> OldValues { get; set; } = new();

    public Dictionary<string, object> NewValues { get; set; } = new();

    public List<string> ChangedFields { get; set; } = new();
}

/// <summary>
/// 物体位置变化事件
/// </summary>
public class ObjectLocationChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;

    public string ObjectId { get; set; } = string.Empty;

    public string OldPosition { get; set; } = string.Empty;

    public string NewPosition { get; set; } = string.Empty;

    public string? TargetPosition { get; set; }
}

/// <summary>
/// 系统启动完成事件
/// </summary>
public class SystemStartedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;

    public DateTime StartTime { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// 系统停止事件
/// </summary>
public class SystemStoppingEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    public string Reason { get; set; } = string.Empty;

    public bool IsGraceful { get; set; }
}

/// <summary>
/// 系统错误事件
/// </summary>
public class SystemErrorEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public Exception? Exception { get; set; }
}

public class TransportRequestedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string? TargetDeviceId { get; set; }
    public string? PalletId { get; set; }
    public int TaskPriority { get; set; } = 2;
    public string? Source { get; set; }
}

public class RawSignalEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;
    public string PlcName { get; set; } = string.Empty;
    public int DbBlock { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Edge { get; set; } = string.Empty;
    public bool ValidatorPassed { get; set; }
    public string? ValidatorReason { get; set; }
    public string? DomainEventType { get; set; }
}
