namespace Wcs.Core.WorkflowCenter;

using Wcs.Core.TaskEngine.Chain;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 业务流程类型
/// </summary>
public enum WorkflowType
{
    /// <summary>入库流程</summary>
    Putaway = 0,
    /// <summary>出库流程</summary>
    Retrieval = 1,
    /// <summary>移库流程</summary>
    Transfer = 2,
    /// <summary>盘点流程</summary>
    Inventory = 3,
    /// <summary>异常回库流程</summary>
    Return = 4,
    /// <summary>空托盘回收</summary>
    EmptyPalletReturn = 5,
    /// <summary>自定义流程</summary>
    Custom = 99
}

/// <summary>
/// 流程状态
/// </summary>
public enum WorkflowStatus
{
    /// <summary>已创建</summary>
    Created = 0,
    /// <summary>执行中</summary>
    Running = 1,
    /// <summary>暂停</summary>
    Paused = 2,
    /// <summary>已完成</summary>
    Completed = 3,
    /// <summary>失败</summary>
    Failed = 4,
    /// <summary>已取消</summary>
    Cancelled = 5
}

/// <summary>
/// 流程定义 — 描述一个业务流程的模板
/// </summary>
public class WorkflowDefinition
{
    /// <summary>流程定义 ID</summary>
    public string DefinitionId { get; set; } = string.Empty;

    /// <summary>流程名称（如 "标准入库流程"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>流程类型</summary>
    public WorkflowType Type { get; set; }

    /// <summary>版本</summary>
    public int Version { get; set; } = 1;

    /// <summary>流程包含的阶段节点列表（顺序执行）</summary>
    public List<WorkflowStage> Stages { get; set; } = new();

    /// <summary>超时（毫秒）— 整流程超时</summary>
    public int TimeoutMs { get; set; } = 300000;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 流程阶段 — 一个阶段可包含多个任务或一个 DAG 图
/// </summary>
public class WorkflowStage
{
    /// <summary>阶段名称（如 "输送线输送"、"提升机转运"、"堆垛机入库"）</summary>
    public string StageName { get; set; } = string.Empty;

    /// <summary>该阶段的 DAG 图（可选）</summary>
    public TaskGraph? Graph { get; set; }

    /// <summary>该阶段包含的任务列表（可选）</summary>
    public List<TaskContext> Tasks { get; set; } = new();

    /// <summary>阶段执行模式：Serial/Parallel</summary>
    public string ExecutionMode { get; set; } = "Serial";

    /// <summary>期望的目标设备能力（如 CanStore）</summary>
    public string? RequiredDeviceCapability { get; set; }

    /// <summary>阶段描述</summary>
    public string? Description { get; set; }
}

/// <summary>
/// 流程实例 — 运行中的业务流程
/// </summary>
public class WorkflowInstance
{
    /// <summary>流程实例 ID</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的流程定义 ID</summary>
    public string DefinitionId { get; set; } = string.Empty;

    /// <summary>流程类型</summary>
    public WorkflowType Type { get; set; }

    /// <summary>当前状态</summary>
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Created;

    /// <summary>当前阶段索引</summary>
    public int CurrentStageIndex { get; set; }

    /// <summary>所有阶段的结果</summary>
    public List<WorkflowStageResult> StageResults { get; set; } = new();

    /// <summary>关联的物料 ID</summary>
    public string? ObjectId { get; set; }

    /// <summary>关联的源位置</summary>
    public string? SourceLocation { get; set; }

    /// <summary>关联的目标位置</summary>
    public string? TargetLocation { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>开始时间</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>完成时间</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 流程阶段执行结果
/// </summary>
public class WorkflowStageResult
{
    public string StageName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
    public int CompletedTasks { get; set; }
    public int TotalTasks { get; set; }
}
