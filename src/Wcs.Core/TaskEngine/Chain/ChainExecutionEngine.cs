namespace Wcs.Core.TaskEngine.Chain;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// DAG 任务链执行引擎 — 管理任务图的拓扑执行、重试、超时、checkpoint
/// 职责单一：接收 TaskGraph，按拓扑序执行节点，返回执行结果
/// </summary>
public class ChainExecutionEngine
{
    private readonly ChainRecoveryService _recoveryService;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<ChainExecutionEngine>? _logger;

    public ChainExecutionEngine(
        ChainRecoveryService recoveryService,
        IEventBus? eventBus = null,
        ILogger<ChainExecutionEngine>? logger = null)
    {
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _eventBus = eventBus;
        _logger = logger;
    }

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

        try
        {
            // 从 checkpoint 恢复（如果有）
            var executionOrder = _recoveryService.ResumeGraph(graph);

            // 准备就绪节点队列
            var readyQueue = new Queue<TaskNode>();
            var completed = new HashSet<string>();
            var inProgress = new HashSet<string>();

            // 初始化：找出所有入度为 0 的节点
            var inDegree = BuildInDegreeMap(graph);
            foreach (var node in executionOrder)
            {
                if (inDegree.TryGetValue(node.NodeId, out var deg) && deg == 0)
                {
                    // 跳过已完成的节点
                    if (graph.TopologicalOrder.Any(n => n.NodeId == node.NodeId &&
                        (_recoveryService.GetCheckpoint(graph.GraphId)?.CompletedNodeIds.Contains(node.NodeId) == true)))
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

                // 执行节点
                var success = await ExecuteNodeWithRetryAsync(graph, node, result, ct);

                inProgress.Remove(node.NodeId);

                if (success)
                {
                    completed.Add(node.NodeId);
                    _recoveryService.CheckpointCompleted(graph.GraphId, node.NodeId);
                    result.CompletedNodes++;

                    // 找到后继节点，检查其前置是否全部完成
                    foreach (var successor in executionOrder)
                    {
                        if (!completed.Contains(successor.NodeId) && !readyQueue.Contains(successor) && !inProgress.Contains(successor.NodeId))
                        {
                            var deps = GetDependencies(successor);
                            if (deps.All(d => completed.Contains(d)))
                            {
                                readyQueue.Enqueue(successor);
                            }
                        }
                    }
                }
                else
                {
                    _recoveryService.CheckpointFailed(graph.GraphId, node.NodeId);
                    result.FailedNodes++;
                    result.ErrorMessage = $"Node '{node.NodeId}' failed after {node.MaxRetries} retries";

                    // DecisionNode 失败不中断整体执行
                    if (node is not DecisionNode)
                        break;
                }
            }

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
    /// 执行单个节点（含重试和超时）
    /// </summary>
    private async Task<bool> ExecuteNodeWithRetryAsync(TaskGraph graph, TaskNode node, TaskGraphResult result, CancellationToken ct)
    {
        // 已完成的节点跳过
        var cp = _recoveryService.GetCheckpoint(graph.GraphId);
        if (cp?.CompletedNodeIds.Contains(node.NodeId) == true)
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
                // 超时后重试
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Node {NodeId} failed on attempt {Attempt}", node.NodeId, attempt);
                // 异常后重试
            }
        }

        return false;
    }

    /// <summary>
    /// 根据节点类型执行具体逻辑
    /// </summary>
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
                return await ExecuteDecisionNodeAsync(graph, decision, ct);

            default:
                _logger?.LogWarning("Unknown node type: {NodeType}", node.GetType().Name);
                return false;
        }
    }

    private Task<bool> ExecuteActionNodeAsync(ActionNode node, CancellationToken ct)
    {
        // ActionNode 由外部处理器执行（如 PLC 写入、API 调用）
        // 这里返回 true — 实际动作由注册的外部委托执行
        _logger?.LogInformation("Action node {NodeId}: type={ActionType}", node.NodeId, node.ActionType);
        return Task.FromResult(true);
    }

    private async Task<bool> ExecuteWaitNodeAsync(WaitNode node, CancellationToken ct)
    {
        if (node.ConditionType == "Delay")
        {
            await Task.Delay(node.PollMs, ct);
            return true;
        }

        // Signal/External 类型需要外部条件满足
        // 当前实现：轮询等待
        var deadline = DateTime.UtcNow.AddMilliseconds(node.TimeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(node.PollMs, ct);
            // 等待外部信号置位 — 由外部调用 MarkConditionMet
        }

        return !ct.IsCancellationRequested;
    }

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

    private Task<bool> ExecuteDecisionNodeAsync(TaskGraph graph, DecisionNode node, CancellationToken ct)
    {
        // 决策条件评估由外部注入的决策器完成
        // 此处返回 true — 实际决策逻辑通过 EventBus 或委托注入
        _logger?.LogInformation("Decision node {NodeId}: expression={Expression}", node.NodeId, node.Expression);
        return Task.FromResult(true);
    }

    private static Dictionary<string, int> BuildInDegreeMap(TaskGraph graph)
    {
        var inDegree = new Dictionary<string, int>();
        foreach (var node in graph.Nodes)
        {
            inDegree[node.NodeId] = 0;
        }
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
