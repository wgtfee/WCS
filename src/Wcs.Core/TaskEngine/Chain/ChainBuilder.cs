namespace Wcs.Core.TaskEngine.Chain;

/// <summary>
/// Fluent API 构建器 — 构建 DAG 任务图
/// 自动进行拓扑排序和环检测
/// </summary>
public class ChainBuilder
{
    private readonly List<TaskNode> _nodes = new();
    private readonly Dictionary<string, TaskNode> _nodeIndex = new();
    private readonly Dictionary<string, List<string>> _adjacency = new(); // nodeId → dependsOnIds
    private TaskChainDefinition? _definition;

    private ChainBuilder() { }

    /// <summary>
    /// 创建一个新的链构建器
    /// </summary>
    public static ChainBuilder Create() => new();

    /// <summary>
    /// 添加动作节点
    /// </summary>
    public ChainBuilder AddAction(string nodeId, string actionType, Dictionary<string, object>? actionParams = null, Action<TaskNode>? configure = null)
    {
        var node = new ActionNode
        {
            NodeId = nodeId,
            ActionType = actionType,
            ActionParams = actionParams ?? new Dictionary<string, object>()
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 添加等待节点（结构化条件）
    /// </summary>
    public ChainBuilder AddWait(string nodeId, WaitCondition condition, Action<TaskNode>? configure = null)
    {
        var node = new WaitNode
        {
            NodeId = nodeId,
            ConditionType = "Signal",
            Condition = condition
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 添加等待节点（表达式条件）
    /// </summary>
    public ChainBuilder AddWait(string nodeId, string conditionType, string conditionExpression, Action<TaskNode>? configure = null)
    {
        var node = new WaitNode
        {
            NodeId = nodeId,
            ConditionType = conditionType,
            ConditionExpression = conditionExpression
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 添加并行节点
    /// </summary>
    public ChainBuilder AddParallel(string nodeId, IEnumerable<string> branchIds, bool waitAll = true, Action<TaskNode>? configure = null)
    {
        var node = new ParallelNode
        {
            NodeId = nodeId,
            BranchNodeIds = branchIds.ToList(),
            WaitAll = waitAll
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 添加延迟节点
    /// </summary>
    public ChainBuilder AddDelay(string nodeId, int delayMs, Action<TaskNode>? configure = null)
    {
        var node = new DelayNode
        {
            NodeId = nodeId,
            DelayMs = delayMs
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 添加决策节点
    /// </summary>
    /// <param name="nodeId">节点 ID</param>
    /// <param name="expression">条件表达式或语义处理器名称（如 "CheckStorageAvailable"、"x > 10"）。
    /// 建议使用业务语义名称而非实现表达式，通过 RegisterDecisionHandler 注册匹配。</param>
    /// <param name="trueBranchId">条件为 true 时的分支节点 ID</param>
    /// <param name="falseBranchId">条件为 false 时的分支节点 ID</param>
    /// <param name="configure">可选配置委托</param>
    public ChainBuilder AddDecision(string nodeId, string expression, string trueBranchId, string falseBranchId, Action<TaskNode>? configure = null)
    {
        var node = new DecisionNode
        {
            NodeId = nodeId,
            Expression = expression,
            TrueBranchNodeId = trueBranchId,
            FalseBranchNodeId = falseBranchId
        };
        configure?.Invoke(node);
        AddNode(node);
        return this;
    }

    /// <summary>
    /// 声明前置依赖
    /// </summary>
    public ChainBuilder DependsOn(string nodeId, string dependsOnId)
    {
        if (!_adjacency.ContainsKey(nodeId))
            _adjacency[nodeId] = new List<string>();
        _adjacency[nodeId].Add(dependsOnId);
        return this;
    }

    /// <summary>
    /// 关联链定义
    /// </summary>
    public ChainBuilder WithDefinition(TaskChainDefinition definition)
    {
        _definition = definition;
        return this;
    }

    /// <summary>
    /// 构建任务图 — 进行拓扑排序和环检测
    /// </summary>
    public TaskGraph Build(string? graphId = null)
    {
        // 建立节点的出边索引（用于 Kahn 算法）
        var inDegree = new Dictionary<string, int>();
        var outEdges = new Dictionary<string, List<string>>();

        foreach (var node in _nodes)
        {
            inDegree[node.NodeId] = 0;
            outEdges[node.NodeId] = new List<string>();
        }

        // 根据 DependsOn 和 _adjacency 建立边
        foreach (var node in _nodes)
        {
            var deps = new List<string>();
            if (_adjacency.TryGetValue(node.NodeId, out var explicitDeps))
                deps.AddRange(explicitDeps);
            deps.AddRange(node.DependsOn);

            foreach (var depId in deps.Distinct())
            {
                if (!outEdges.ContainsKey(depId))
                    throw new InvalidOperationException($"Node '{node.NodeId}' depends on unknown node '{depId}'");

                outEdges[depId].Add(node.NodeId);
                inDegree[node.NodeId]++;
            }
        }

        // Kahn 拓扑排序
        var queue = new Queue<string>();
        foreach (var kvp in inDegree)
        {
            if (kvp.Value == 0)
                queue.Enqueue(kvp.Key);
        }

        var sorted = new List<TaskNode>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            sorted.Add(_nodeIndex[currentId]);

            foreach (var neighbor in outEdges[currentId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        // 环检测
        if (sorted.Count != _nodes.Count)
        {
            var cyclic = _nodes.Select(n => n.NodeId)
                .Except(sorted.Select(n => n.NodeId))
                .ToList();
            throw new InvalidOperationException($"Cycle detected involving nodes: {string.Join(", ", cyclic)}");
        }

        return new TaskGraph
        {
            GraphId = graphId ?? Guid.NewGuid().ToString("N"),
            Nodes = _nodes.AsReadOnly(),
            NodeIndex = new Dictionary<string, TaskNode>(_nodeIndex),
            TopologicalOrder = sorted.AsReadOnly(),
            Version = _definition?.Version,
            DefinitionId = _definition?.DefinitionId
        };
    }

    private void AddNode(TaskNode node)
    {
        if (_nodeIndex.ContainsKey(node.NodeId))
            throw new InvalidOperationException($"Duplicate node ID: {node.NodeId}");

        _nodes.Add(node);
        _nodeIndex[node.NodeId] = node;
    }
}
