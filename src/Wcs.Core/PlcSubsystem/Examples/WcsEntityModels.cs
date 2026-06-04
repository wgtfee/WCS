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
    public string? DeviceId { get; set; }
    public string? RouteId { get; set; }
    public string? PalletId { get; set; }

    /// <summary>Created=0 / Queued=1 / Running=2 / Completed=4 / Failed=5</summary>
    public int Status { get; set; }
    public int Priority { get; set; } = 2;
    public DateTime CreatedTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    /// <summary>Task引擎当前执行的节点ID（ACTION或WAIT）</summary>
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
    public string? Route { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public long TotalDurationMs { get; set; }

    /// <summary>经过节点JSON: [{"Node":"CV01","DwellMs":3000}, ...]</summary>
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
    public string? TaskId { get; set; }

    /// <summary>Created=0 Sent=1 Acked=2 Executing=3 Done=4 Completed=5 Failed=6 Timeout=7</summary>
    public int Status { get; set; }
    public string? Payload { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime? SentTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public int TimeoutMs { get; set; } = 5000;
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
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangeTime { get; set; }
    public string? PlcName { get; set; }
    public int DbBlock { get; set; }
    public bool ValidatorPassed { get; set; }
    public string? ValidatorReason { get; set; }
}
