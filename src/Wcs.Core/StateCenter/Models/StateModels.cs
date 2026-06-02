namespace Wcs.Core.StateCenter.Models;

/// <summary>
/// 设备状态
/// </summary>
public class DeviceState
{
    public string DeviceId { get; set; } = string.Empty;
    
    public DeviceStatusEnum Status { get; set; }
    
    public DateTime LastUpdateTime { get; set; }
    
    public string? CurrentPosition { get; set; }
    
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// 任务运行时状态
/// </summary>
public class TaskRuntime
{
    public string TaskId { get; set; } = string.Empty;
    
    public TaskStatusEnum Status { get; set; }
    
    public int Priority { get; set; }
    
    public string RouteId { get; set; } = string.Empty;
    
    public DateTime CreatedTime { get; set; }
    
    public DateTime? StartTime { get; set; }
    
    public DateTime? EndTime { get; set; }
    
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// 报警状态
/// </summary>
public class AlarmState
{
    public string AlarmId { get; set; } = string.Empty;
    
    public string AlarmCode { get; set; } = string.Empty;
    
    public AlarmStatusEnum Status { get; set; }
    
    public AlarmLevelEnum Level { get; set; }
    
    public string Message { get; set; } = string.Empty;
    
    public DateTime OccurTime { get; set; }
    
    public DateTime? RecoverTime { get; set; }
}

/// <summary>
/// 物体/物料状态
/// </summary>
public class ObjectState
{
    public string ObjectId { get; set; } = string.Empty;
    
    public string CurrentPosition { get; set; } = string.Empty;
    
    public string? TargetPosition { get; set; }
    
    public ObjectStatusEnum Status { get; set; }
    
    public DateTime UpdateTime { get; set; }
    
    public Dictionary<string, object> Attributes { get; set; } = new();
}

/// <summary>
/// PLC 数据块状态
/// </summary>
public class PlcBlockState
{
    public string BlockName { get; set; } = string.Empty;
    
    public Dictionary<string, object> Values { get; set; } = new();
    
    public DateTime LastReadTime { get; set; }
    
    public bool IsValid { get; set; }
}

/// <summary>
/// 设备状态枚举
/// </summary>
public enum DeviceStatusEnum
{
    Offline = 0,
    Online = 1,
    Running = 2,
    Idle = 3,
    Error = 4,
    Maintenance = 5
}

/// <summary>
/// 任务状态枚举
/// </summary>
public enum TaskStatusEnum
{
    Created = 0,
    Queued = 1,
    Running = 2,
    Paused = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
    Recovered = 7
}

/// <summary>
/// 报警状态枚举
/// </summary>
public enum AlarmStatusEnum
{
    Active = 0,
    Acknowledged = 1,
    Recovered = 2
}

/// <summary>
/// 报警级别枚举
/// </summary>
public enum AlarmLevelEnum
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

/// <summary>
/// 物体状态枚举
/// </summary>
public enum ObjectStatusEnum
{
    Idle = 0,
    Moving = 1,
    Processing = 2,
    Completed = 3,
    Error = 4
}
