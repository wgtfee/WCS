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
/// 报警状态枚举（5 状态生命周期的扁平映射）
/// </summary>
public enum AlarmStatusEnum
{
    /// <summary>正常 / 无报警</summary>
    Normal = 0,
    /// <summary>Pending — 延迟确认中（防抖期间）</summary>
    PendingRaise = 1,
    /// <summary>报警已激活</summary>
    Active = 2,
    /// <summary>操作员已确认</summary>
    Acknowledged = 3,
    /// <summary>PendingRecover — 恢复延迟确认中</summary>
    PendingRecover = 4,
    /// <summary>已恢复</summary>
    Recovered = 5
}

/// <summary>
/// 报警规则配置 — 每种 AlarmCode 对应一条规则
/// </summary>
public class AlarmRule
{
    public string AlarmCode { get; set; } = string.Empty;
    public AlarmLevelEnum Level { get; set; } = AlarmLevelEnum.Warning;
    public int DelayRaiseMs { get; set; } = 3000;       // 防抖确认时间
    public int DelayRecoverMs { get; set; } = 5000;     // 防抖恢复时间
    public bool AutoAck { get; set; } = false;           // 自动确认
    public int SuppressionWindowSec { get; set; } = 60;  // 风暴抑制窗口（秒）
    public int SuppressionThreshold { get; set; } = 10;  // 窗口内触发次数阈值
    public string? AlarmGroup { get; set; }              // 聚合分组（同组做根因归并）
}

/// <summary>
/// 报警聚合分组键
/// </summary>
public record AlarmGroupKey(string DeviceId, string AlarmGroup);

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

/// <summary>
/// 系统概览 DTO
/// </summary>
public class SystemOverview
{
    public int DeviceCount { get; set; }
    public int ActiveTaskCount { get; set; }
    public int ActiveAlarmCount { get; set; }
    public int TrackedObjectCount { get; set; }
    public int ActiveLockCount { get; set; }
}
