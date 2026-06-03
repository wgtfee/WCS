namespace Wcs.Core.ExecutionHistoryCenter;

using System.Collections.Concurrent;

/// <summary>
/// 执行历史中心实现 — 运输执行追溯
///
/// 记录每个托盘的完整运输轨迹：
/// 谁（PalletId）→ 从哪（SourceNode）→ 到哪（TargetNode）
/// → 经过哪些节点 → 各节点耗时 → 最终是否成功
/// </summary>
public class ExecutionHistoryCenter : IExecutionHistoryCenter
{
    private readonly ConcurrentDictionary<string, TransportExecutionRecord> _records = new();
    private readonly ConcurrentDictionary<string, List<string>> _palletIndex = new(); // palletId → taskIds
    private readonly ConcurrentDictionary<string, List<string>> _nodeIndex = new();   // nodeId → taskIds
    private const int MaxRecords = 50000;

    public string CreateRecord(string taskId, string palletId, string sourceNode, string targetNode)
    {
        var record = new TransportExecutionRecord
        {
            TaskId = taskId,
            PalletId = palletId,
            SourceNode = sourceNode,
            TargetNode = targetNode,
            StartTime = DateTime.UtcNow
        };

        _records[taskId] = record;

        // 物料索引
        var palletTasks = _palletIndex.GetOrAdd(palletId, _ => new List<string>());
        lock (palletTasks) { palletTasks.Add(taskId); }

        return taskId;
    }

    public void RecordNodeArrival(string taskId, string nodeId)
    {
        if (!_records.TryGetValue(taskId, out var record)) return;

        var visit = new NodeVisitRecord { NodeId = nodeId, ArriveTime = DateTime.UtcNow };
        record.NodeVisits.Add(visit);
        record.Route.Add(nodeId);

        // 节点索引
        var nodeTasks = _nodeIndex.GetOrAdd(nodeId, _ => new List<string>());
        lock (nodeTasks) { if (!nodeTasks.Contains(taskId)) nodeTasks.Add(taskId); }
    }

    public void RecordNodeDeparture(string taskId, string nodeId)
    {
        if (!_records.TryGetValue(taskId, out var record)) return;

        var visit = record.NodeVisits.LastOrDefault(v => v.NodeId == nodeId && v.LeaveTime == null);
        if (visit != null)
            visit.LeaveTime = DateTime.UtcNow;
    }

    public void CompleteRecord(string taskId, bool success, string? failureReason = null)
    {
        if (!_records.TryGetValue(taskId, out var record)) return;

        record.Success = success;
        record.FailureReason = failureReason;
        record.EndTime = DateTime.UtcNow;

        // 如果最后一个节点未记录离开，自动标记
        var lastVisit = record.NodeVisits.LastOrDefault(v => v.LeaveTime == null);
        if (lastVisit != null)
            lastVisit.LeaveTime = record.EndTime;

        // 容量控制
        if (_records.Count > MaxRecords)
            Cleanup(TimeSpan.FromDays(30));
    }

    public TransportExecutionRecord? GetRecord(string taskId)
    {
        _records.TryGetValue(taskId, out var record);
        return record;
    }

    public IReadOnlyList<TransportExecutionRecord> GetPalletHistory(string palletId, int maxRecords = 50)
    {
        if (!_palletIndex.TryGetValue(palletId, out var taskIds))
            return Array.Empty<TransportExecutionRecord>();

        lock (taskIds)
        {
            return taskIds
                .Select(id => _records.TryGetValue(id, out var r) ? r : null)
                .Where(r => r != null)
                .OrderByDescending(r => r!.StartTime)
                .Take(maxRecords)
                .ToList()!;
        }
    }

    public IReadOnlyList<TransportExecutionRecord> GetRecordsByNode(string nodeId, int maxRecords = 50)
    {
        if (!_nodeIndex.TryGetValue(nodeId, out var taskIds))
            return Array.Empty<TransportExecutionRecord>();

        lock (taskIds)
        {
            return taskIds
                .Select(id => _records.TryGetValue(id, out var r) ? r : null)
                .Where(r => r != null)
                .OrderByDescending(r => r!.StartTime)
                .Take(maxRecords)
                .ToList()!;
        }
    }

    public IReadOnlyList<TransportExecutionRecord> GetRecentRecords(int maxRecords = 100)
    {
        return _records.Values
            .OrderByDescending(r => r.StartTime)
            .Take(maxRecords)
            .ToList();
    }

    public ExecutionHistoryStats GetStats()
    {
        var all = _records.Values;
        var completed = all.Where(r => r.EndTime.HasValue).ToList();

        return new ExecutionHistoryStats
        {
            TotalRecords = all.Count,
            SuccessCount = all.Count(r => r.Success),
            FailureCount = all.Count(r => !r.Success && r.EndTime.HasValue),
            AvgDurationMs = completed.Count > 0 ? completed.Average(r => r.TotalDurationMs) : 0,
            NodeTraffic = _nodeIndex.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count)
        };
    }

    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = _records.Values
            .Where(r => r.EndTime.HasValue && r.EndTime.Value < cutoff)
            .Select(r => r.TaskId)
            .ToList();

        foreach (var taskId in toRemove)
        {
            if (_records.TryRemove(taskId, out var record))
            {
                // 清理索引
                if (_palletIndex.TryGetValue(record.PalletId, out var taskIds))
                {
                    lock (taskIds) { taskIds.Remove(taskId); }
                }
                foreach (var visit in record.NodeVisits)
                {
                    if (_nodeIndex.TryGetValue(visit.NodeId, out var nodeTasks))
                    {
                        lock (nodeTasks) { nodeTasks.Remove(taskId); }
                    }
                }
            }
        }

        return toRemove.Count;
    }

    public int Count => _records.Count;
}
