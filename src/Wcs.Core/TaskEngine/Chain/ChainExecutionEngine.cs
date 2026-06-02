namespace Wcs.Core.TaskEngine.Chain;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// DAG 任务链执行引擎 — 管理任务图的拓扑执行、重试、超时、checkpoint
/// 支持 DecisionNode 条件分支路由和 WaitNode 事件驱动
/// </summary>
public class ChainExecutionEngine
{
    private readonly ChainRecoveryService _recoveryService;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<ChainExecutionEngine>? _logger;

    // DecisionNode 委托注册表：Expression → handler
    private readonly ConcurrentDictionary<string, Func<DecisionNode, CancellationToken, Task<bool>>> _decisionHandlers = new();

    // WaitNode 委托注册表：ConditionType → handler
    private readonly ConcurrentDictionary<string, Func<WaitNode, CancellationToken, Task<bool>>> _waitHandlers = new();

    public ChainExecutionEngine(
        ChainRecoveryService recoveryService,
        IEventBus? eventBus = null,
        ILogger<ChainExecutionEngine>? logger = null)
    {
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _eventBus = eventBus;
        _logger = logger;

        // 注册默认的 Signal 等待处理器
        RegisterWaitConditionHandler("Signal", ExecuteWaitSignalAsync);
    }

    // ==================== 委托注册 ====================

    /// <summary>
    /// 注册 DecisionNode 条件评估器
    /// </summary>
    public void RegisterDecisionHandler(string expression, Func<DecisionNode, CancellationToken, Task<bool>> handler)
    {
        _decisionHandlers[expression] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// 注册 WaitNode 条件等待处理器
    /// </summary>
    public void RegisterWaitConditionHandler(string conditionType, Func<WaitNode, CancellationToken, Task<bool>> handler)
    {
        _waitHandlers[conditionType] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    // ==================== 主执行循环 ====================

    /// <summary>
    /// 执行整个 DAG 任务图
    /// </summary>
    public async Task<TaskGraphResult> ExecuteAsync(TaskGraph graph, CancellationToken ct = default)
    {
        var result = new TaskGraphResult
        {
            GraphId = graph.GraphId,
            StartTime = DateTime.UtcNow,
            TotalNodes = graph.Nodes.Count
        };

        var prunedNodes = new HashSet<string>(); // 未选中分支的节点（被剪枝）

        try
        {
            var executionOrder = _recoveryService.ResumeGraph(graph);
            var readyQueue = new Queue<TaskNode>();
            var completed = new HashSet<string>();
            var inProgress = new HashSet<string>();

            // 初始化：找出所有入度为 0 的节点
            var inDegree = BuildInDegreeMap(graph);
            foreach (var node in executionOrder)
            {
                if (inDegree.TryGetValue(node.NodeId, out var deg) && deg == 0)
                {
                    if (IsNodeCheckpointed(graph, node))
                    {
                        completed.Add(node.NodeId);
                        result.SkippedNodes++;
                        continue;
                    }
                    readyQueue.Enqueue(node);
                }
            }

            while (readyQueue.Count > 0 && !ct.IsCancellationRequested)
            {
                var node = readyQueue.Dequeue();
                inProgress.Add(node.NodeId);

                var success = await ExecuteNodeWithRetryAsync(graph, node, result, ct);
                inProgress.Remove(node.NodeId);

                if (success)
                {
                    completed.Add(node.NodeId);
                    _recoveryService.CheckpointCompleted(graph.GraphId, node.NodeId);
                    result.CompletedNodes++;

                    // DecisionNode 特殊处理：分支路由
                    if (node is DecisionNode decision)
                    {
                        var chosenBranchId = success ? decision.TrueBranchNodeId : decision.FalseBranchNodeId;
                        var unchosenBranchId = success ? decision.FalseBranchNodeId : decision.TrueBranchNodeId;

                        if (!string.IsNullOrEmpty(unchosenBranchId))
                            prunedNodes.Add(unchosenBranchId);

                        // 只 enqueue 选中分支的根节点（如果其所有前置已完成）
                        if (!string.IsNullOrEmpty(chosenBranchId))
                        {
                            var chosenNode = graph.NodeIndex.TryGetValue(chosenBranchId, out var cn) ? cn : null;
                            if (chosenNode != null && !completed.Contains(chosenNode.NodeId))
                            {
                                var deps = GetDependencies(chosenNode);
                                if (deps.All(d => completed.Contains(d) || prunedNodes.Contains(d)))
                                {
                                    readyQueue.Enqueue(chosenNode);
                                }
                            }
                        }
                    }
                    else
                    {
                        // 非 DecisionNode：标准后继查找
                        EnqueueReadySuccessors(executionOrder, completed, inProgress, readyQueue, prunedNodes);
                    }
                }
                else
                {
                    _recoveryService.CheckpointFailed(graph.GraphId, node.NodeId);
                    result.FailedNodes++;
                    result.ErrorMessage = $"Node '{node.NodeId}' failed after {node.MaxRetries} retries";

                    if (node is not DecisionNode)
                        break;
                }
            }

            // 将剪枝节点计入 SkippedNodes
            result.SkippedNodes += prunedNodes.Count(n => !completed.Contains(n));
            result.Success = result.FailedNodes == 0 && result.CompletedNodes > 0;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Execution cancelled";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Chain execution failed for graph {GraphId}", graph.GraphId);
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.EndTime = DateTime.UtcNow;
            if (result.Success)
                _recoveryService.MarkComplete(graph.GraphId);
        }

        return result;
    }

    /// <summary>
    /// 标准后继查找 — 将前置依赖全部完成（或被剪枝）的节点加入就绪队列
    /// </summary>
    private void EnqueueReadySuccessors(
        IReadOnlyList<TaskNode> executionOrder,
        HashSet<string> completed,
        HashSet<string> inProgress,
        Queue<TaskNode> readyQueue,
        HashSet<string> prunedNodes)
    {
        foreach (var successor in executionOrder)
        {
            if (completed.Contains(successor.NodeId) || prunedNodes.Contains(successor.NodeId)
                || readyQueue.Contains(successor) || inProgress.Contains(successor.NodeId))
                continue;

            var deps = GetDependencies(successor);
            // 如果有依赖被剪枝，当前节点也剪枝（传递性）
            if (deps.Any(d => prunedNodes.Contains(d)))
            {
                prunedNodes.Add(successor.NodeId);
                continue;
            }

            if (deps.All(d => completed.Contains(d)))
            {
                readyQueue.Enqueue(successor);
            }
        }
    }

    // ==================== 节点执行 ====================

    private async Task<bool> ExecuteNodeWithRetryAsync(TaskGraph graph, TaskNode node, TaskGraphResult result, CancellationToken ct)
    {
        if (IsNodeCheckpointed(graph, node))
            return true;

        for (int attempt = 0; attempt <= node.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                _logger?.LogWarning("Retrying node {NodeId} attempt {Attempt}/{MaxRetries}",
                    node.NodeId, attempt, node.MaxRetries);
                result.TotalRetries++;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(node.TimeoutMs);

                var success = await ExecuteNodeAsync(graph, node, timeoutCts.Token);

                if (success)
                {
                    node.RetryCount = attempt;
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("Node {NodeId} timed out after {TimeoutMs}ms",
                    node.NodeId, node.TimeoutMs);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Node {NodeId} failed on attempt {Attempt}", node.NodeId, attempt);
            }
        }

        return false;
    }

    private async Task<bool> ExecuteNodeAsync(TaskGraph graph, TaskNode node, CancellationToken ct)
    {
        switch (node)
        {
            case ActionNode action:
                return await ExecuteActionNodeAsync(action, ct);
            case DelayNode delay:
                await Task.Delay(delay.DelayMs, ct);
                return true;
            case WaitNode wait:
                return await ExecuteWaitNodeAsync(wait, ct);
            case ParallelNode parallel:
                return await ExecuteParallelNodeAsync(graph, parallel, ct);
            case DecisionNode decision:
                return await ExecuteDecisionNodeAsync(decision, ct);
            default:
                _logger?.LogWarning("Unknown node type: {NodeType}", node.GetType().Name);
                return false;
        }
    }

    private Task<bool> ExecuteActionNodeAsync(ActionNode node, CancellationToken ct)
    {
        _logger?.LogInformation("Action node {NodeId}: type={ActionType}", node.NodeId, node.ActionType);
        return Task.FromResult(true);
    }

    // ==================== WaitNode 事件驱动 ====================

    private async Task<bool> ExecuteWaitNodeAsync(WaitNode node, CancellationToken ct)
    {
        if (node.ConditionType == "Delay")
        {
            await Task.Delay(node.PollMs, ct);
            return true;
        }

        if (_waitHandlers.TryGetValue(node.ConditionType, out var handler))
        {
            return await handler(node, ct);
        }

        _logger?.LogWarning("No handler registered for WaitNode condition type: {ConditionType}", node.ConditionType);
        return false;
    }

    /// <summary>
    /// 默认 Signal 等待处理器 — 通过 EventBus 订阅 DeviceStateChangedEvent
    /// ConditionExpression 格式: "DeviceId:ExpectedStatus"
    /// 例如: "CV_101:Running" 表示等待 CV_101 设备进入 Running 状态
    /// </summary>
    private async Task<bool> ExecuteWaitSignalAsync(WaitNode node, CancellationToken ct)
    {
        if (_eventBus == null)
        {
            _logger?.LogWarning("WaitNode {NodeId}: EventBus not available, falling back to polling", node.NodeId);
            await Task.Delay(node.PollMs, ct);
            return true;
        }

        var tcs = new TaskCompletionSource<bool>();
        using var ctReg = ct.Register(() => tcs.TrySetCanceled());

        // 优先使用结构化 Condition，兼容旧字符串格式
        string targetDevice, targetStatus;
        if (node.Condition != null)
        {
            targetDevice = node.Condition.DeviceId;
            targetStatus = node.Condition.ExpectedStatus;
        }
        else
        {
            // 解析条件表达式: "DeviceId:ExpectedStatus"
            var parts = node.ConditionExpression?.Split(':', 2) ?? Array.Empty<string>();
            targetDevice = parts.Length > 0 ? parts[0] : "";
            targetStatus = parts.Length > 1 ? parts[1] : "Running";
        }

        var handler = new DeviceStateEventHandler(tcs, targetDevice, targetStatus);
        _eventBus.Subscribe(handler);

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(node.TimeoutMs), ct);
        }
        catch
        {
            return false;
        }
        finally
        {
            _eventBus.Unsubscribe(handler);
        }
    }

    /// <summary>
    /// 设备状态事件处理器 — 用于 WaitNode 等待特定设备状态
    /// </summary>
    private sealed class DeviceStateEventHandler : IEventHandler<DeviceStateChangedEvent>
    {
        private readonly TaskCompletionSource<bool> _tcs;
        private readonly string _targetDevice;
        private readonly string _targetStatus;
        private bool _handled;

        public DeviceStateEventHandler(TaskCompletionSource<bool> tcs, string targetDevice, string targetStatus)
        {
            _tcs = tcs;
            _targetDevice = targetDevice;
            _targetStatus = targetStatus;
        }

        public Task HandleAsync(DeviceStateChangedEvent @event, CancellationToken cancellationToken)
        {
            if (!_handled)
            {
                if (string.IsNullOrEmpty(_targetDevice) || @event.DeviceId == _targetDevice)
                {
                    if (@event.NewStatus.ToString() == _targetStatus || string.IsNullOrEmpty(_targetStatus))
                    {
                        _handled = true;
                        _tcs.TrySetResult(true);
                    }
                }
            }
            return Task.CompletedTask;
        }
    }

    // ==================== ParallelNode ====================

    private async Task<bool> ExecuteParallelNodeAsync(TaskGraph graph, ParallelNode node, CancellationToken ct)
    {
        var branchNodes = node.BranchNodeIds
            .Select(id => graph.NodeIndex.TryGetValue(id, out var n) ? n : null)
            .Where(n => n != null)
            .ToList();

        if (branchNodes.Count == 0)
        {
            _logger?.LogWarning("Parallel node {NodeId} has no valid branches", node.NodeId);
            return false;
        }

        var tasks = branchNodes.Select(bn => ExecuteNodeWithRetryAsync(graph, bn!, new TaskGraphResult(), ct));

        if (node.WaitAll)
        {
            var results = await Task.WhenAll(tasks);
            return results.All(r => r);
        }
        else
        {
            var first = await Task.WhenAny(tasks);
            return await first;
        }
    }

    // ==================== DecisionNode 条件分支 ====================

    private async Task<bool> ExecuteDecisionNodeAsync(DecisionNode node, CancellationToken ct)
    {
        // 查注册表执行条件评估
        if (_decisionHandlers.TryGetValue(node.Expression, out var handler))
        {
            var result = await handler(node, ct);
            _logger?.LogInformation("Decision node {NodeId}: expression={Expression} evaluated to {Result}",
                node.NodeId, node.Expression, result);
            return result;
        }

        if (_decisionHandlers.Count > 0)
        {
            _logger?.LogWarning("Decision node {NodeId}: no handler registered for expression '{Expression}', " +
                "trying default handler", node.NodeId, node.Expression);

            // 尝试默认 handler（如果有）
            if (_decisionHandlers.TryGetValue("*", out var defaultHandler))
                return await defaultHandler(node, ct);
        }

        _logger?.LogWarning("Decision node {NodeId}: no handler for expression '{Expression}', defaulting to true",
            node.NodeId, node.Expression);
        return true;
    }

    // ==================== 辅助方法 ====================

    private bool IsNodeCheckpointed(TaskGraph graph, TaskNode node)
    {
        var cp = _recoveryService.GetCheckpoint(graph.GraphId);
        return cp?.CompletedNodeIds.Contains(node.NodeId) == true;
    }

    private static Dictionary<string, int> BuildInDegreeMap(TaskGraph graph)
    {
        var inDegree = new Dictionary<string, int>();
        foreach (var node in graph.Nodes)
            inDegree[node.NodeId] = 0;

        foreach (var node in graph.Nodes)
        {
            foreach (var depId in node.DependsOn)
            {
                if (inDegree.ContainsKey(depId))
                    inDegree[node.NodeId]++;
            }
        }
        return inDegree;
    }

    private static List<string> GetDependencies(TaskNode node)
    {
        return node.DependsOn ?? new List<string>();
    }
}

/// <summary>
/// 任务图执行结果
/// </summary>
public class TaskGraphResult
{
    public string GraphId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    public int TotalNodes { get; set; }
    public int CompletedNodes { get; set; }
    public int FailedNodes { get; set; }
    public int SkippedNodes { get; set; }
    public int TotalRetries { get; set; }
    public string? ErrorMessage { get; set; }
}
