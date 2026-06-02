namespace Wcs.Core.TaskEngine.Chain;

/// <summary>
/// DAG 任务节点基类 — 所有节点类型从此继承
/// </summary>
public abstract record TaskNode
{
    /// <summary>节点唯一标识</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>可读标签</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>当前重试次数</summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>最大重试次数</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>节点超时（毫秒）</summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>前置依赖节点 ID 列表</summary>
    public List<string> DependsOn { get; init; } = new();
}

/// <summary>
/// 动作节点 — 调用外部动作（PLC 写、API 调用、脚本执行）
/// </summary>
public record ActionNode : TaskNode
{
    /// <summary>动作类型：PlcWrite / HttpCall / Script</summary>
    public string ActionType { get; init; } = string.Empty;

    /// <summary>动作参数</summary>
    public Dictionary<string, object> ActionParams { get; init; } = new();
}

/// <summary>
/// 结构化等待条件 — 替代 ConditionExpression 字符串解析
/// </summary>
public record WaitCondition
{
    /// <summary>设备 ID（如 "CV01"）</summary>
    public string DeviceId { get; init; } = string.Empty;
    /// <summary>期望状态（如 "Ready", "Running"）</summary>
    public string ExpectedStatus { get; init; } = string.Empty;
    /// <summary>可选：等待命名事件信号</summary>
    public string? SignalName { get; init; }
}

/// <summary>
/// 等待节点 — 等待条件满足后继续
/// </summary>
public record WaitNode : TaskNode
{
    /// <summary>条件类型：Signal / Delay / External</summary>
    public string ConditionType { get; init; } = "Signal";

    /// <summary>条件表达式（PLC 地址或表达式），兼容旧格式 "DeviceId:ExpectedStatus"</summary>
    public string ConditionExpression { get; init; } = string.Empty;

    /// <summary>结构化条件（优先于 ConditionExpression）</summary>
    public WaitCondition? Condition { get; init; }

    /// <summary>轮询间隔（毫秒）</summary>
    public int PollMs { get; init; } = 500;
}

/// <summary>
/// 并行节点 — 并行执行多个分支
/// </summary>
public record ParallelNode : TaskNode
{
    /// <summary>并行执行的子节点 ID 列表</summary>
    public List<string> BranchNodeIds { get; init; } = new();

    /// <summary>true=等待所有完成, false=任一完成继续</summary>
    public bool WaitAll { get; init; } = true;
}

/// <summary>
/// 延迟节点 — 等待指定时间后继续
/// </summary>
public record DelayNode : TaskNode
{
    /// <summary>延迟毫秒数</summary>
    public int DelayMs { get; init; }
}

/// <summary>
/// 决策节点 — 根据条件表达式选择分支
/// </summary>
public record DecisionNode : TaskNode
{
    /// <summary>条件表达式</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>条件为 true 时的分支节点 ID</summary>
    public string TrueBranchNodeId { get; init; } = string.Empty;

    /// <summary>条件为 false 时的分支节点 ID</summary>
    public string FalseBranchNodeId { get; init; } = string.Empty;
}

/// <summary>
/// DAG 任务图 — 由 TaskNode 组成的有向无环图
/// </summary>
public class TaskGraph
{
    /// <summary>图 ID</summary>
    public string GraphId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>所有节点</summary>
    public IReadOnlyList<TaskNode> Nodes { get; init; } = Array.Empty<TaskNode>();

    /// <summary>节点检索索引</summary>
    public IReadOnlyDictionary<string, TaskNode> NodeIndex { get; init; } = new Dictionary<string, TaskNode>();

    /// <summary>拓扑排序后的执行顺序</summary>
    public IReadOnlyList<TaskNode> TopologicalOrder { get; init; } = Array.Empty<TaskNode>();

    /// <summary>链定义版本</summary>
    public Version? Version { get; init; }

    /// <summary>关联的 TaskChainDefinition ID</summary>
    public string? DefinitionId { get; init; }
}

/// <summary>
/// 节点执行状态
/// </summary>
public enum NodeExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped  // 恢复时跳过已完成的节点
}
