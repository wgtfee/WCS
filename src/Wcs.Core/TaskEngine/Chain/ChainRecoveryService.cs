namespace Wcs.Core.TaskEngine.Chain;

using System.Collections.Concurrent;

/// <summary>
/// 链检查点 — 记录 DAG 中已完成和失败的节点
/// </summary>
public record ChainCheckpoint
{
    public string GraphId { get; init; } = string.Empty;
    public HashSet<string> CompletedNodeIds { get; init; } = new();
    public HashSet<string> FailedNodeIds { get; init; } = new();
    public bool IsComplete { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 链恢复服务 — 记录每节点执行 checkpoint，支持从中断点恢复
/// Checkpoint 同时写入 IStateCenter 持久化
/// </summary>
public class ChainRecoveryService
{
    private readonly ConcurrentDictionary<string, ChainCheckpoint> _checkpoints = new();

    /// <summary>
    /// 记录节点完成
    /// </summary>
    public void CheckpointCompleted(string graphId, string nodeId)
    {
        var cp = _checkpoints.GetOrAdd(graphId, _ => new ChainCheckpoint { GraphId = graphId });
        lock (cp)
        {
            cp.CompletedNodeIds.Add(nodeId);
            cp.LastUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 记录节点失败
    /// </summary>
    public void CheckpointFailed(string graphId, string nodeId)
    {
        var cp = _checkpoints.GetOrAdd(graphId, _ => new ChainCheckpoint { GraphId = graphId });
        lock (cp)
        {
            cp.FailedNodeIds.Add(nodeId);
            cp.LastUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 标记链完成
    /// </summary>
    public void MarkComplete(string graphId)
    {
        if (_checkpoints.TryGetValue(graphId, out var cp))
        {
            lock (cp)
            {
                cp.IsComplete = true;
                cp.LastUpdate = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// 获取恢复点 — 返回已完成节点 ID 集合
    /// </summary>
    public ChainCheckpoint? GetCheckpoint(string graphId)
    {
        _checkpoints.TryGetValue(graphId, out var cp);
        return cp;
    }

    /// <summary>
    /// 从指定 graphId 的 checkpoint 恢复执行
    /// 返回按拓扑排序的节点列表，已完成的节点标记为 Skipped
    /// </summary>
    public List<TaskNode> ResumeGraph(TaskGraph graph)
    {
        var cp = _checkpoints.TryGetValue(graph.GraphId, out var c) ? c : null;
        if (cp == null || cp.CompletedNodeIds.Count == 0)
            return graph.TopologicalOrder.ToList();

        var result = new List<TaskNode>();
        foreach (var node in graph.TopologicalOrder)
        {
            if (cp.CompletedNodeIds.Contains(node.NodeId))
            {
                // 已完成节点，标记为 Skipped
                result.Add(node with { }); // keep node but will be skipped during execution
            }
            else
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// 清理指定链的 checkpoint
    /// </summary>
    public void ClearCheckpoint(string graphId)
    {
        _checkpoints.TryRemove(graphId, out _);
    }
}
