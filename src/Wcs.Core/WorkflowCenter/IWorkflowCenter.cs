namespace Wcs.Core.WorkflowCenter;

/// <summary>
/// 流程中心接口 — 管理业务流程的定义、实例化和执行
///
/// Workflow 是业务流程维度的抽象，位于 Task 之上：
/// Workflow → 多个 TaskContext → 每个 Task 可关联 ChainExecutionEngine 的 DAG 图
/// </summary>
public interface IWorkflowCenter
{
    /// <summary>
    /// 注册流程定义
    /// </summary>
    void RegisterDefinition(WorkflowDefinition definition);

    /// <summary>
    /// 获取流程定义
    /// </summary>
    WorkflowDefinition? GetDefinition(string definitionId);

    /// <summary>
    /// 获取所有流程定义
    /// </summary>
    IReadOnlyList<WorkflowDefinition> GetDefinitions(WorkflowType? type = null);

    /// <summary>
    /// 启动流程实例
    /// </summary>
    Task<WorkflowInstance> StartWorkflowAsync(string definitionId,
        string? objectId = null, string? sourceLocation = null, string? targetLocation = null,
        CancellationToken ct = default);

    /// <summary>
    /// 获取流程实例状态
    /// </summary>
    WorkflowInstance? GetInstance(string instanceId);

    /// <summary>
    /// 获取当前运行中的流程实例
    /// </summary>
    IEnumerable<WorkflowInstance> GetActiveInstances();

    /// <summary>
    /// 取消流程
    /// </summary>
    Task<bool> CancelWorkflowAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// 暂停流程
    /// </summary>
    Task<bool> PauseWorkflowAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// 恢复流程
    /// </summary>
    Task<bool> ResumeWorkflowAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// 流程中心统计
    /// </summary>
    WorkflowCenterStats GetStats();
}

/// <summary>
/// 流程中心统计
/// </summary>
public class WorkflowCenterStats
{
    public int DefinitionCount { get; set; }
    public int ActiveInstanceCount { get; set; }
    public int TotalCompleted { get; set; }
    public int TotalFailed { get; set; }
    public double AvgCompletionTimeMs { get; set; }
}
