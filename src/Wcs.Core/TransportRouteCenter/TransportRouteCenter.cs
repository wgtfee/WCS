namespace Wcs.Core.RouteCenter;

using System.Collections.Concurrent;
using Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 运输路由中心 — 设备级路径规划、故障避障、拥塞控制
///
/// 在 TopologyGraph 基础上增加动态路由能力：
/// - 拥塞检测（边占用计数）
/// - 故障设备节点自动绕行
/// - 多策略寻路（最短/最空/平衡）
/// - 运输路径占用管理
///
/// 纯 WCS 边界：只做设备 A→设备 B 的运输路径规划
/// </summary>
public sealed class TransportRouteCenter : ITransportRouteCenter
{
    private readonly TopologyGraph _graph;
    private readonly ConcurrentDictionary<string, int> _edgeOccupancy = new();
    private readonly ConcurrentDictionary<string, string> _edgeOccupiedBy = new();
    private readonly ConcurrentDictionary<string, bool> _faultedNodes = new();
    private readonly ConcurrentDictionary<string, bool> _faultedEdges = new();
    private readonly ConcurrentDictionary<string, DateTime> _edgeTimestamps = new();

    private long _totalRoutes;
    private long _totalFailures;

    public TransportRouteCenter(TopologyGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public TransportRouteResult FindRoute(TransportRouteRequest request)
    {
        Interlocked.Increment(ref _totalRoutes);

        if (!_graph.HasNode(request.FromNodeId))
            return TransportRouteResult.NotFound($"FromNode '{request.FromNodeId}' not found");
        if (!_graph.HasNode(request.ToNodeId))
            return TransportRouteResult.NotFound($"ToNode '{request.ToNodeId}' not found");

        if (_faultedNodes.TryGetValue(request.FromNodeId, out var fromFaulted) && fromFaulted)
            return TransportRouteResult.NotFound($"FromNode '{request.FromNodeId}' is faulted");
        if (_faultedNodes.TryGetValue(request.ToNodeId, out var toFaulted) && toFaulted)
            return TransportRouteResult.NotFound($"ToNode '{request.ToNodeId}' is faulted");

        var result = FindPathWithStrategy(request);

        if (!result.Found)
            Interlocked.Increment(ref _totalFailures);

        return result;
    }

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

    public void ReleasePath(IReadOnlyList<string> edgeIds, string? objectId)
    {
        foreach (var edgeId in edgeIds)
        {
            _edgeOccupancy.AddOrUpdate(edgeId, 0, (_, count) => Math.Max(0, count - 1));
            if (objectId != null)
                _edgeOccupiedBy.TryRemove(edgeId, out _);
        }
    }

    public void MarkNodeFault(string nodeId, bool isFaulted)
    {
        if (isFaulted) _faultedNodes[nodeId] = true;
        else _faultedNodes.TryRemove(nodeId, out _);
    }

    public void MarkEdgeFault(string edgeId, bool isFaulted)
    {
        if (isFaulted) _faultedEdges[edgeId] = true;
        else _faultedEdges.TryRemove(edgeId, out _);
    }

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

    public void Reset()
    {
        _edgeOccupancy.Clear();
        _edgeOccupiedBy.Clear();
        _faultedNodes.Clear();
        _faultedEdges.Clear();
        _edgeTimestamps.Clear();
    }

    public TransportRouteStats GetStats()
    {
        return new TransportRouteStats
        {
            TotalRoutesCalculated = (int)Interlocked.Read(ref _totalRoutes),
            TotalRouteFailures = (int)Interlocked.Read(ref _totalFailures),
            FaultedNodes = _faultedNodes.Count,
            FaultedEdges = _faultedEdges.Count,
            CongestedEdges = _edgeOccupancy.Count(kvp => kvp.Value >= 2)
        };
    }

    private TransportRouteResult FindPathWithStrategy(TransportRouteRequest request)
    {
        var faultedNodes = new HashSet<string>(
            _faultedNodes.Where(kvp => kvp.Value).Select(kvp => kvp.Key));
        var faultedEdges = new HashSet<string>(
            _faultedEdges.Where(kvp => kvp.Value).Select(kvp => kvp.Key));

        var queue = new Queue<(string NodeId, int Weight)>();
        var visited = new Dictionary<string, (string? PrevNode, string? EdgeId, int Weight)>();
        var congestionCache = new Dictionary<string, int>();

        visited[request.FromNodeId] = (null, null, 0);
        queue.Enqueue((request.FromNodeId, 0));

        while (queue.Count > 0)
        {
            var (current, currentWeight) = queue.Dequeue();
            if (current == request.ToNodeId) break;

            foreach (var edge in _graph.GetOutgoingEdges(current))
            {
                if (faultedEdges.Contains(edge.EdgeId)) continue;
                if (edge.IsOccupied) continue;

                var next = edge.ToNodeId;
                if (faultedNodes.Contains(next)) continue;

                if (request.RequiredCapability.HasValue &&
                    (edge.Capability & request.RequiredCapability.Value) != request.RequiredCapability.Value)
                    continue;

                var congestionWeight = GetCongestionWeight(edge.EdgeId, congestionCache, request.Strategy);
                var newWeight = currentWeight + edge.Weight + congestionWeight;

                if (visited.TryGetValue(next, out var existing) && existing.Weight <= newWeight)
                    continue;

                visited[next] = (current, edge.EdgeId, newWeight);
                queue.Enqueue((next, newWeight));
            }
        }

        if (!visited.TryGetValue(request.ToNodeId, out _))
            return TransportRouteResult.NotFound("No path found (obstacle or congestion)");

        return ReconstructPath(request, visited, faultedNodes);
    }

    private int GetCongestionWeight(string edgeId, Dictionary<string, int> cache, TransportRouteStrategy strategy)
    {
        if (strategy == TransportRouteStrategy.Shortest) return 0;
        if (!cache.TryGetValue(edgeId, out var weight))
        {
            var count = _edgeOccupancy.TryGetValue(edgeId, out var c) ? c : 0;
            weight = strategy switch
            {
                TransportRouteStrategy.LeastCongested => count * 10,
                TransportRouteStrategy.Balanced => count * 3,
                _ => 0
            };
            cache[edgeId] = weight;
        }
        return weight;
    }

    private TransportRouteResult ReconstructPath(TransportRouteRequest request,
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
            if (!visited.TryGetValue(current, out var info) || info.PrevNode == null) break;
            edgePath.Add(info.EdgeId!);
            current = info.PrevNode;
        }

        nodePath.Reverse();
        edgePath.Reverse();

        foreach (var fn in faultedNodes)
        {
            if (_graph.HasNode(fn)) bypassed.Add(fn);
        }

        var maxCongestion = edgePath
            .Select(e => (int)GetCongestion(e))
            .DefaultIfEmpty(0).Max();

        return new TransportRouteResult
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
