namespace Wcs.Core.RouteCenter;

using System.Collections.Concurrent;
using Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 动态路由中心 — 寻路、避障、拥塞控制
///
/// 在 TopologyGraph 的基础上增加：
/// - 动态拥塞检测
/// - 故障节点/边自动回避
/// - 多策略寻路（最短/最空/平衡）
/// - 路径占用管理
/// </summary>
public sealed class RouteCenter : IRouteCenter
{
    private readonly TopologyGraph _graph;
    private readonly ConcurrentDictionary<string, int> _edgeOccupancy = new();
    private readonly ConcurrentDictionary<string, string> _edgeOccupiedBy = new();
    private readonly ConcurrentDictionary<string, bool> _faultedNodes = new();
    private readonly ConcurrentDictionary<string, bool> _faultedEdges = new();
    private readonly ConcurrentDictionary<string, DateTime> _edgeTimestamps = new();

    private long _totalRoutes;
    private long _totalFailures;
    private readonly object _statsLock = new();

    private const int CongestionThresholdPerEdge = 2;

    public RouteCenter(TopologyGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    /// <summary>
    /// 计算最优路径 — 自动避开故障节点/边，考虑拥塞
    /// </summary>
    public RouteResult FindRoute(RouteRequest request)
    {
        lock (_statsLock) _totalRoutes++;

        // 1. 检查起点终点有效性
        if (!_graph.HasNode(request.FromNodeId))
            return RouteResult.NotFound($"FromNode '{request.FromNodeId}' not found");
        if (!_graph.HasNode(request.ToNodeId))
            return RouteResult.NotFound($"ToNode '{request.ToNodeId}' not found");

        // 2. 检查故障节点
        if (_faultedNodes.TryGetValue(request.FromNodeId, out var fromFaulted) && fromFaulted)
            return RouteResult.NotFound($"FromNode '{request.FromNodeId}' is faulted");
        if (_faultedNodes.TryGetValue(request.ToNodeId, out var toFaulted) && toFaulted)
            return RouteResult.NotFound($"ToNode '{request.ToNodeId}' is faulted");

        // 3. BFS 寻路（考虑拥塞权重）
        var result = FindPathWithStrategy(request);

        if (!result.Found)
        {
            lock (_statsLock) _totalFailures++;
        }

        return result;
    }

    /// <summary>
    /// 标记路径占用的边
    /// </summary>
    public void OccupyPath(IReadOnlyList<string> edgeIds, string? objectId)
    {
        foreach (var edgeId in edgeIds)
        {
            _edgeOccupancy.AddOrUpdate(edgeId, 1, (_, count) => count + 1);
            if (objectId != null)
                _edgeOccupiedBy[edgeId] = objectId;
            _edgeTimestamps[edgeId] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 释放路径占用的边
    /// </summary>
    public void ReleasePath(IReadOnlyList<string> edgeIds, string? objectId)
    {
        foreach (var edgeId in edgeIds)
        {
            _edgeOccupancy.AddOrUpdate(edgeId, 0, (_, count) => Math.Max(0, count - 1));
            if (objectId != null)
                _edgeOccupiedBy.TryRemove(edgeId, out _);
        }
    }

    /// <summary>
    /// 标记节点故障
    /// </summary>
    public void MarkNodeFault(string nodeId, bool isFaulted)
    {
        if (isFaulted)
            _faultedNodes[nodeId] = true;
        else
            _faultedNodes.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// 标记边故障
    /// </summary>
    public void MarkEdgeFault(string edgeId, bool isFaulted)
    {
        if (isFaulted)
            _faultedEdges[edgeId] = true;
        else
            _faultedEdges.TryRemove(edgeId, out _);
    }

    /// <summary>
    /// 获取拥塞状态
    /// </summary>
    public CongestionLevel GetCongestion(string edgeId)
    {
        var count = _edgeOccupancy.TryGetValue(edgeId, out var c) ? c : 0;
        return count switch
        {
            0 => CongestionLevel.Clear,
            1 => CongestionLevel.Light,
            2 => CongestionLevel.Moderate,
            _ => CongestionLevel.Heavy
        };
    }

    /// <summary>
    /// 获取全图拥塞报告
    /// </summary>
    public IReadOnlyList<CongestionRecord> GetCongestionReport()
    {
        return _edgeOccupancy
            .Where(kvp => kvp.Value > 0)
            .Select(kvp => new CongestionRecord
            {
                EdgeId = kvp.Key,
                OccupiedCount = kvp.Value,
                Level = GetCongestion(kvp.Key),
                LastUpdate = _edgeTimestamps.TryGetValue(kvp.Key, out var ts) ? ts : DateTime.MinValue
            })
            .OrderByDescending(r => r.OccupiedCount)
            .ToList();
    }

    /// <summary>
    /// 清空所有动态状态
    /// </summary>
    public void Reset()
    {
        _edgeOccupancy.Clear();
        _edgeOccupiedBy.Clear();
        _faultedNodes.Clear();
        _faultedEdges.Clear();
        _edgeTimestamps.Clear();
    }

    /// <summary>
    /// 统计信息
    /// </summary>
    public RouteCenterStats GetStats()
    {
        return new RouteCenterStats
        {
            AvgPathLength = 0,
            TotalRoutesCalculated = (int)Interlocked.Read(ref _totalRoutes),
            TotalRouteFailures = (int)Interlocked.Read(ref _totalFailures),
            FaultedNodes = _faultedNodes.Count,
            FaultedEdges = _faultedEdges.Count,
            CongestedEdges = _edgeOccupancy.Count(kvp => kvp.Value >= CongestionThresholdPerEdge)
        };
    }

    // ==================== 内部寻路 ====================

    private RouteResult FindPathWithStrategy(RouteRequest request)
    {
        // 收集故障节点用于排除
        var faultedNodes = new HashSet<string>(
            _faultedNodes.Where(kvp => kvp.Value).Select(kvp => kvp.Key));
        var faultedEdges = new HashSet<string>(
            _faultedEdges.Where(kvp => kvp.Value).Select(kvp => kvp.Key));

        // BFS 带拥塞权重的寻路
        var queue = new Queue<(string NodeId, int Weight)>();
        var visited = new Dictionary<string, (string? PrevNode, string? EdgeId, int Weight)>();
        var congestionCache = new Dictionary<string, int>();

        visited[request.FromNodeId] = (null, null, 0);
        queue.Enqueue((request.FromNodeId, 0));

        while (queue.Count > 0)
        {
            var (current, currentWeight) = queue.Dequeue();
            if (current == request.ToNodeId)
                break;

            var outgoing = _graph.GetOutgoingEdges(current);
            foreach (var edge in outgoing)
            {
                // 跳过故障边
                if (faultedEdges.Contains(edge.EdgeId))
                    continue;

                // 跳过被占用的边
                if (edge.IsOccupied)
                    continue;

                var next = edge.ToNodeId;

                // 跳过故障节点
                if (faultedNodes.Contains(next))
                    continue;

                // 能力过滤
                if (request.RequiredCapability.HasValue &&
                    (edge.Capability & request.RequiredCapability.Value) != request.RequiredCapability.Value)
                    continue;

                // 计算拥塞附加权重
                var congestionWeight = GetCongestionWeight(edge.EdgeId, congestionCache, request.Strategy);
                var newWeight = currentWeight + edge.Weight + congestionWeight;

                if (visited.TryGetValue(next, out var existing) && existing.Weight <= newWeight)
                    continue;

                visited[next] = (current, edge.EdgeId, newWeight);
                queue.Enqueue((next, newWeight));
            }
        }

        // 回溯路径
        if (!visited.TryGetValue(request.ToNodeId, out _))
            return RouteResult.NotFound("No path found (obstacle or congestion)");

        return ReconstructPath(request, visited, faultedNodes);
    }

    private int GetCongestionWeight(string edgeId, Dictionary<string, int> cache, RouteStrategy strategy)
    {
        if (strategy == RouteStrategy.Shortest)
            return 0;

        if (!cache.TryGetValue(edgeId, out var weight))
        {
            var count = _edgeOccupancy.TryGetValue(edgeId, out var c) ? c : 0;
            weight = strategy switch
            {
                RouteStrategy.LeastCongested => count * 10,
                RouteStrategy.Balanced => count * 3,
                _ => 0
            };
            cache[edgeId] = weight;
        }
        return weight;
    }

    private RouteResult ReconstructPath(RouteRequest request,
        Dictionary<string, (string? PrevNode, string? EdgeId, int Weight)> visited,
        HashSet<string> faultedNodes)
    {
        var nodePath = new List<string>();
        var edgePath = new List<string>();
        var bypassed = new List<string>();

        var current = request.ToNodeId;
        while (current != null)
        {
            nodePath.Add(current);
            if (!visited.TryGetValue(current, out var info) || info.PrevNode == null)
                break;
            edgePath.Add(info.EdgeId!);
            current = info.PrevNode;
        }

        nodePath.Reverse();
        edgePath.Reverse();

        // 检测绕过的故障节点
        foreach (var fn in faultedNodes)
        {
            if (_graph.HasNode(fn))
                bypassed.Add(fn);
        }

        // 计算拥塞级别
        var maxCongestion = edgePath
            .Select(e => (int)GetCongestion(e))
            .DefaultIfEmpty(0)
            .Max();

        return new RouteResult
        {
            Found = true,
            NodePath = nodePath,
            EdgePath = edgePath,
            TotalWeight = visited.TryGetValue(request.ToNodeId, out var v) ? v.Weight : 0,
            Congestion = (CongestionLevel)maxCongestion,
            BypassedNodes = bypassed.Count > 0 ? bypassed : null
        };
    }
}
