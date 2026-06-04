using SqlSugar;

namespace Wcs.Core.PlcSubsystem.Examples;

// ====================================================================
// 数据库表实体 — 任务状态和执行历史
// SqlSugar 自动建表（CodeFirst）
// ====================================================================

/// <summary>任务运行记录表 — TaskScheduler 中每个任务的完整状态</summary>
[SugarTable("Wcs_TaskRun")]
public class TaskRunEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string TaskId { get; set; } = string.Empty;
        [SugarColumn(IsNullable = true)]
    public string? DeviceId { get; set; }
        [SugarColumn(IsNullable = true)]
    public string? RouteId { get; set; }
        [SugarColumn(IsNullable = true)]
    public string? PalletId { get; set; }

    /// <summary>Created=0 / Queued=1 / Running=2 / Completed=4 / Failed=5</summary>
    public int Status { get; set; }
    public int Priority { get; set; } = 2;
    public DateTime CreatedTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public DateTime? StartTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Task引擎当前执行的节点ID（ACTION或WAIT）</summary>
       [SugarColumn(IsNullable = true)]
   public string? CurrentNodeId { get; set; }
}

/// <summary>运输执行历史表 — 每个托盘的运输记录</summary>
[SugarTable("Wcs_TransportHistory")]
public class TransportHistoryEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string TaskId { get; set; } = string.Empty;
    public string PalletId { get; set; } = string.Empty;
    public string SourceNode { get; set; } = string.Empty;
    public string TargetNode { get; set; } = string.Empty;
        [SugarColumn(IsNullable = true)]
    public string? Route { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
        [SugarColumn(IsNullable = true)]
    public string? FailureReason { get; set; }
    public long TotalDurationMs { get; set; }

    /// <summary>经过节点JSON: [{"Node":"CV01","DwellMs":3000}, ...]</summary>
    [SugarColumn(IsNullable = true)]
    public string? NodeVisitsJson { get; set; }
}

/// <summary>命令执行记录表</summary>
[SugarTable("Wcs_CommandLog")]
public class CommandLogEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
        [SugarColumn(IsNullable = true)]
    public string? TaskId { get; set; }

    /// <summary>Created=0 Sent=1 Acked=2 Executing=3 Done=4 Completed=5 Failed=6 Timeout=7</summary>
    public int Status { get; set; }
         [SugarColumn(IsNullable = true)]
    public string? Payload { get; set; }
    public DateTime CreatedTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public DateTime? SentTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public DateTime? CompletedTime { get; set; }
    public int TimeoutMs { get; set; } = 5000;
        [SugarColumn(IsNullable = true)]
    public string? ErrorMessage { get; set; }
}

/// <summary>设备状态变更日志表</summary>
[SugarTable("Wcs_DeviceStateLog")]
public class DeviceStateLogEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? OldValue { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? NewValue { get; set; }
    public DateTime ChangeTime { get; set; }
        [SugarColumn(IsNullable = true)]
    public string? PlcName { get; set; }
    public int DbBlock { get; set; }
    public bool ValidatorPassed { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? DomainEventType { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? ValidatorReason { get; set; }
}

/// <summary>PLC 写入记录表 — 记录每次写入 PLC 的操作</summary>
[SugarTable("Wcs_PlcWriteLog")]
public class PlcWriteLogEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }
    public string PlcName { get; set; } = string.Empty;
    public int DbBlock { get; set; }
    public int StartByte { get; set; }
    public string CommandType { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? DeviceId { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? TaskId { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? DataHex { get; set; }
    public int DataLength { get; set; }
    public bool Success { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? ErrorMessage { get; set; }
    public DateTime WriteTime { get; set; } = DateTime.UtcNow;
}

// ====================================================================
// 以下为原 EF Core 实体转为 SqlSugar CodeFirst
// (Former: WcsDbContext → DbSet)
// ====================================================================

/// <summary>设备运行时表 — 持久化 StateCenter 中的设备状态</summary>
[SugarTable("Wcs_DeviceRuntime")]
public class DeviceRuntimeEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string DeviceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdateTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? Properties { get; set; }
}

/// <summary>任务运行时表 — 持久化 StateCenter 中的任务状态</summary>
[SugarTable("Wcs_TaskRuntime")]
public class TaskRuntimeEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string TaskId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Priority { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? RouteId { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? StartTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? Parameters { get; set; }
}

/// <summary>报警运行时表 — 持久化 StateCenter 中的报警状态</summary>
[SugarTable("Wcs_AlarmRuntime")]
public class AlarmRuntimeEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string AlarmId { get; set; } = string.Empty;
    public string AlarmCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? Message { get; set; }
    public DateTime OccurTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? RecoverTime { get; set; }
}

/// <summary>任务历史表 — 完成任务归档</summary>
[SugarTable("Wcs_TaskHistory")]
public class TaskHistoryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? RouteId { get; set; }
    public int Priority { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? StartTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? ErrorMessage { get; set; }
}

/// <summary>报警历史表</summary>
[SugarTable("Wcs_AlarmHistory")]
public class AlarmHistoryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public string AlarmCode { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? Level { get; set; }
    [SugarColumn(IsNullable = true)]
    public string? Message { get; set; }
    public DateTime StartTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
}

/// <summary>任务事件表 — 只追加</summary>
[SugarTable("Wcs_TaskEvent")]
public class TaskEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? Payload { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
}
