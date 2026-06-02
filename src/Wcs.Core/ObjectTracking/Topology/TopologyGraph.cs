using System.Collections.Concurrent;

namespace Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 拓扑图快照 — 用于保存和恢复拓扑结构的不可变快照
/// </summary>
public record TopologySnapshot
{
    public IReadOnlyDictionary<string, Zone> Zones { get; init; } = new Dictionary<string, Zone>();
    public IReadOnlyDictionary<string, Node> Nodes { get; init; } = new Dictionary<string, Node>();
    public IReadOnlyDictionary<string, Edge> Edges { get; init; } = new Dictionary<string, Edge>();
}

/// <summary>
/// BFS 最短路径结果
/// </summary>
public record PathResult
{
    /// <summary>路径是否存在</summary>
    public bool Found { get; init; }

    /// <summary>经过的节点 ID 列表（含起点和终点）</summary>
    public IReadOnlyList<string> NodePath { get; init; } = Array.Empty<string>();

    /// <summary>经过的边 ID 列表</summary>
    public IReadOnlyList<string> EdgePath { get; init; } = Array.Empty<string>();

    /// <summary>路径总权重</summary>
    public int TotalWeight { get; init; }

    /// <summary>空路径（未找到）</summary>
    public static PathResult NotFound { get; } = new PathResult { Found = false };
}

/// <summary>
/// 拓扑图 — 管理区域、节点、边的有向图，支持路径规划和可达性查询。
/// 线程安全：所有公开成员支持并发调用。
/// </summary>
public class TopologyGraph
{
    // ==================== 核心存储 ====================

    private readonly ConcurrentDictionary<string, Zone> _zones = new();
    private readonly ConcurrentDictionary<string, Node> _nodes = new();
    private readonly ConcurrentDictionary<string, Edge> _edges = new();

    // 邻接表：nodeId → 出边 ID 集合
    private readonly ConcurrentDictionary<string, HashSet<string>> _outgoingEdges = new();
    // 邻接表：nodeId → 入边 ID 集合
    private readonly ConcurrentDictionary<string, HashSet<string>> _incomingEdges = new();

    private readonly object _adjacencyLock = new();

    // ==================== 区域操作 ====================

    /// <summary>所有区域</summary>
    public IReadOnlyCollection<Zone> Zones => _zones.Values.ToList();

    /// <summary>所有节点</summary>
    public IReadOnlyCollection<Node> Nodes => _nodes.Values.ToList();

    /// <summary>所有边</summary>
    public IReadOnlyCollection<Edge> Edges => _edges.Values.ToList();

    /// <summary>
    /// 添加区域。如果区域 ID 已存在则返回 false。
    /// </summary>
    public bool AddZone(Zone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(zone.ZoneId))
            throw new ArgumentException("ZoneId 不能为空", nameof(zone));

        return _zones.TryAdd(zone.ZoneId, zone);
    }

    /// <summary>
    /// 获取区域，不存在则返回 null。
    /// </summary>
    public Zone? GetZone(string zoneId)
    {
        _zones.TryGetValue(zoneId, out var zone);
        return zone;
    }

    /// <summary>
    /// 移除区域及其下所有节点和边。
    /// </summary>
    public bool RemoveZone(string zoneId)
    {
        if (!_zones.TryRemove(zoneId, out _))
            return false;

        // 移除该区域下的所有节点
        var zoneNodes = _nodes.Values
            .Where(n => n.ZoneId == zoneId)
            .Select(n => n.NodeId)
            .ToList();

        foreach (var nodeId in zoneNodes)
        {
            RemoveNode(nodeId);
        }

        return true;
    }

    /// <summary>
    /// 区域是否存在。
    /// </summary>
    public bool HasZone(string zoneId) => _zones.ContainsKey(zoneId);

    // ==================== 节点操作 ====================

    /// <summary>
    /// 添加节点。如果节点 ID 已存在或所属区域不存在则返回 false。
    /// </summary>
    public bool AddNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.NodeId))
            throw new ArgumentException("NodeId 不能为空", nameof(node));

        // 验证区域存在（如已指定）
        if (!string.IsNullOrWhiteSpace(node.ZoneId) && !_zones.ContainsKey(node.ZoneId))
            return false;

        if (!_nodes.TryAdd(node.NodeId, node))
            return false;

        // 初始化邻接表
        _outgoingEdges.GetOrAdd(node.NodeId, _ => new HashSet<string>());
        _incomingEdges.GetOrAdd(node.NodeId, _ => new HashSet<string>());

        return true;
    }

    /// <summary>
    /// 获取节点，不存在则返回 null。
    /// </summary>
    public Node? GetNode(string nodeId)
    {
        _nodes.TryGetValue(nodeId, out var node);
        return node;
    }

    /// <summary>
    /// 移除节点及其关联的所有边。
    /// </summary>
    public bool RemoveNode(string nodeId)
    {
        if (!_nodes.TryRemove(nodeId, out _))
            return false;

        // 收集与该节点相关的所有边
        List<string> edgesToRemove = new();

        if (_outgoingEdges.TryRemove(nodeId, out var outgoing))
        {
            lock (_adjacencyLock)
            {
                edgesToRemove.AddRange(outgoing);
            }
        }

        if (_incomingEdges.TryRemove(nodeId, out var incoming))
        {
            lock (_adjacencyLock)
            {
                edgesToRemove.AddRange(incoming);
            }
        }

        // 从对方的邻接表中移除此节点的引用
        foreach (var edgeId in edgesToRemove)
        {
            if (_edges.TryRemove(edgeId, out var edge))
            {
                // 从目标节点的入边表中移除
                if (!string.IsNullOrEmpty(edge.ToNodeId) &&
                    _incomingEdges.TryGetValue(edge.ToNodeId, out var toIncoming))
                {
                    lock (_adjacencyLock) { toIncoming.Remove(edgeId); }
                }

                // 从源节点的出边表中移除
                if (!string.IsNullOrEmpty(edge.FromNodeId) &&
                    _outgoingEdges.TryGetValue(edge.FromNodeId, out var fromOutgoing))
                {
                    lock (_adjacencyLock) { fromOutgoing.Remove(edgeId); }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 节点是否存在。
    /// </summary>
    public bool HasNode(string nodeId) => _nodes.ContainsKey(nodeId);

    // ==================== 边操作 ====================

    /// <summary>
    /// 添加有向边。如果边 ID 已存在、源或目标节点不存在则返回 false。
    /// </summary>
    public bool AddEdge(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (string.IsNullOrWhiteSpace(edge.EdgeId))
            throw new ArgumentException("EdgeId 不能为空", nameof(edge));
        if (string.IsNullOrWhiteSpace(edge.FromNodeId))
            throw new ArgumentException("FromNodeId 不能为空", nameof(edge));
        if (string.IsNullOrWhiteSpace(edge.ToNodeId))
            throw new ArgumentException("ToNodeId 不能为空", nameof(edge));

        // 验证节点存在
        if (!_nodes.ContainsKey(edge.FromNodeId) || !_nodes.ContainsKey(edge.ToNodeId))
            return false;

        if (!_edges.TryAdd(edge.EdgeId, edge))
            return false;

        // 更新邻接表
        var outgoing = _outgoingEdges.GetOrAdd(edge.FromNodeId, _ => new HashSet<string>());
        var incoming = _incomingEdges.GetOrAdd(edge.ToNodeId, _ => new HashSet<string>());

        lock (_adjacencyLock)
        {
            outgoing.Add(edge.EdgeId);
            incoming.Add(edge.EdgeId);
        }

        return true;
    }

    /// <summary>
    /// 获取边，不存在则返回 null。
    /// </summary>
    public Edge? GetEdge(string edgeId)
    {
        _edges.TryGetValue(edgeId, out var edge);
        return edge;
    }

    /// <summary>
    /// 移除边。
    /// </summary>
    public bool RemoveEdge(string edgeId)
    {
        if (!_edges.TryRemove(edgeId, out var edge))
            return false;

        // 从邻接表中移除
        if (!string.IsNullOrEmpty(edge.FromNodeId) &&
            _outgoingEdges.TryGetValue(edge.FromNodeId, out var outgoing))
        {
            lock (_adjacencyLock) { outgoing.Remove(edgeId); }
        }

        if (!string.IsNullOrEmpty(edge.ToNodeId) &&
            _incomingEdges.TryGetValue(edge.ToNodeId, out var incoming))
        {
            lock (_adjacencyLock) { incoming.Remove(edgeId); }
        }

        return true;
    }

    /// <summary>
    /// 边是否存在。
    /// </summary>
    public bool HasEdge(string edgeId) => _edges.ContainsKey(edgeId);

    // ==================== 邻接查询 ====================

    /// <summary>
    /// 获取从指定节点出发的出边。
    /// </summary>
    public IReadOnlyList<Edge> GetOutgoingEdges(string nodeId)
    {
        if (!_outgoingEdges.TryGetValue(nodeId, out var edgeIds))
            return Array.Empty<Edge>();

        lock (_adjacencyLock)
        {
            return edgeIds
                .Select(id => _edges.TryGetValue(id, out var e) ? e : null)
                .Where(e => e != null)
                .ToList()!;
        }
    }

    /// <summary>
    /// 获取到达指定节点的入边。
    /// </summary>
    public IReadOnlyList<Edge> GetIncomingEdges(string nodeId)
    {
        if (!_incomingEdges.TryGetValue(nodeId, out var edgeIds))
            return Array.Empty<Edge>();

        lock (_adjacencyLock)
        {
            return edgeIds
                .Select(id => _edges.TryGetValue(id, out var e) ? e : null)
                .Where(e => e != null)
                .ToList()!;
        }
    }

    /// <summary>
    /// 获取指定节点的所有邻接节点（直接相连）。
    /// </summary>
    public IReadOnlyList<string> GetNeighbors(string nodeId)
    {
        var neighbors = new List<string>();
        var outgoing = GetOutgoingEdges(nodeId);
        foreach (var edge in outgoing)
        {
            if (!string.IsNullOrEmpty(edge.ToNodeId))
                neighbors.Add(edge.ToNodeId);
        }
        return neighbors;
    }

    // ==================== BFS 最短路径 ====================

    /// <summary>
    /// 使用 BFS 查找从 fromNodeId 到 toNodeId 的最短路径（按边权重计）。
    /// </summary>
    public PathResult GetShortestPath(string fromNodeId, string toNodeId)
    {
        return GetShortestPath(fromNodeId, toNodeId, null, true);
    }

    /// <summary>
    /// 使用 BFS 查找最短路径，支持能力过滤和排除占用边。
    /// </summary>
    /// <param name="fromNodeId">起点节点 ID</param>
    /// <param name="toNodeId">终点节点 ID</param>
    /// <param name="requiredCapability">可选的能力过滤（只走包含指定能力的边）</param>
    /// <param name="avoidOccupied">是否避开已占用的边，默认 true</param>
    public PathResult GetShortestPath(
        string fromNodeId,
        string toNodeId,
        EdgeCapability? requiredCapability = null,
        bool avoidOccupied = true)
    {
        if (!_nodes.ContainsKey(fromNodeId) || !_nodes.ContainsKey(toNodeId))
            return PathResult.NotFound;

        if (fromNodeId == toNodeId)
            return new PathResult
            {
                Found = true,
                NodePath = new[] { fromNodeId },
                EdgePath = Array.Empty<string>(),
                TotalWeight = 0
            };

        // BFS 队列 — 存储 (nodeId, accumulatedWeight)
        var queue = new Queue<(string NodeId, int Weight)>();
        var visited = new Dictionary<string, int>
        {
            [fromNodeId] = 0
        };

        // 前驱追踪：nodeId → (predecessorNodeId, edgeId)
        var predecessor = new Dictionary<string, (string NodeId, string EdgeId)>();

        queue.Enqueue((fromNodeId, 0));

        while (queue.Count > 0)
        {
            var (current, currentWeight) = queue.Dequeue();

            var outgoingEdges = GetOutgoingEdges(current);

            foreach (var edge in outgoingEdges)
            {
                // 跳过占用边
                if (avoidOccupied && edge.IsOccupied)
                    continue;

                // 能力过滤
                if (requiredCapability.HasValue &&
                    (edge.Capability & requiredCapability.Value) != requiredCapability.Value)
                    continue;

                var next = edge.ToNodeId;
                if (string.IsNullOrEmpty(next))
                    continue;

                var newWeight = currentWeight + edge.Weight;

                // 如果节点已访问且已有更优路径则跳过
                if (visited.TryGetValue(next, out var bestWeight) && bestWeight <= newWeight)
                    continue;

                visited[next] = newWeight;
                predecessor[next] = (current, edge.EdgeId);
                queue.Enqueue((next, newWeight));

                // 早期退出：到达目标时可继续 BFS 以找更优路径
                // 但在无权图中（所有 weight=1），首次到达即最优
                // 有权图中仍需遍历
            }
        }

        // 如果目标节点不可达
        if (!predecessor.ContainsKey(toNodeId))
            return PathResult.NotFound;

        // 回溯构建路径
        return ReconstructPath(fromNodeId, toNodeId, predecessor, visited[toNodeId]);
    }

    /// <summary>
    /// 查找所有可能路径（简化版 — 限制最大条数和最大深度防止爆炸）。
    /// </summary>
    public IReadOnlyList<PathResult> GetAllPaths(
        string fromNodeId,
        string toNodeId,
        int maxResults = 5,
        int maxDepth = 20,
        bool avoidOccupied = true)
    {
        var results = new List<PathResult>();
        var currentPath = new List<string>();     // node ids
        var currentEdges = new List<string>();    // edge ids
        var visited = new HashSet<string>();

        void Dfs(string current)
        {
            if (results.Count >= maxResults)
                return;

            if (currentPath.Count > maxDepth)
                return;

            if (current == toNodeId && currentPath.Count > 1)
            {
                int totalWeight = currentEdges
                    .Select(id => _edges.TryGetValue(id, out var e) ? e.Weight : 1)
                    .Sum();

                results.Add(new PathResult
                {
                    Found = true,
                    NodePath = currentPath.ToList(),
                    EdgePath = currentEdges.ToList(),
                    TotalWeight = totalWeight
                });
                return;
            }

            var outgoing = GetOutgoingEdges(current);
            foreach (var edge in outgoing)
            {
                if (avoidOccupied && edge.IsOccupied)
                    continue;

                var next = edge.ToNodeId;
                if (string.IsNullOrEmpty(next) || visited.Contains(next))
                    continue;

                visited.Add(next);
                currentPath.Add(next);
                currentEdges.Add(edge.EdgeId);

                Dfs(next);

                currentEdges.RemoveAt(currentEdges.Count - 1);
                currentPath.RemoveAt(currentPath.Count - 1);
                visited.Remove(next);
            }
        }

        visited.Add(fromNodeId);
        currentPath.Add(fromNodeId);
        Dfs(fromNodeId);

        return results;
    }

    // ==================== 可达性查询 ====================

    /// <summary>
    /// 从指定节点出发可达的所有节点。
    /// </summary>
    public IReadOnlySet<string> GetReachableNodes(string fromNodeId, bool avoidOccupied = true)
    {
        if (!_nodes.ContainsKey(fromNodeId))
            return new HashSet<string>();

        var reachable = new HashSet<string>();
        var visited = new HashSet<string> { fromNodeId };
        var queue = new Queue<string>();

        queue.Enqueue(fromNodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var edges = GetOutgoingEdges(current);

            foreach (var edge in edges)
            {
                if (avoidOccupied && edge.IsOccupied)
                    continue;

                var next = edge.ToNodeId;
                if (string.IsNullOrEmpty(next) || visited.Contains(next))
                    continue;

                visited.Add(next);
                reachable.Add(next);
                queue.Enqueue(next);
            }
        }

        return reachable;
    }

    /// <summary>
    /// 从 fromNodeId 是否能到达 toNodeId。
    /// </summary>
    public bool IsReachable(string fromNodeId, string toNodeId, bool avoidOccupied = true)
    {
        return GetShortestPath(fromNodeId, toNodeId, null, avoidOccupied).Found;
    }

    // ==================== 区域查询 ====================

    /// <summary>
    /// 获取指定区域内的所有节点。
    /// </summary>
    public IReadOnlyList<Node> GetZoneNodes(string zoneId)
    {
        return _nodes.Values
            .Where(n => n.ZoneId == zoneId)
            .ToList();
    }

    /// <summary>
    /// 获取指定区域内的所有边（边的任一节点属于该区域）。
    /// </summary>
    public IReadOnlyList<Edge> GetZoneEdges(string zoneId)
    {
        var zoneNodeIds = new HashSet<string>(
            _nodes.Values.Where(n => n.ZoneId == zoneId).Select(n => n.NodeId));

        return _edges.Values
            .Where(e => zoneNodeIds.Contains(e.FromNodeId) || zoneNodeIds.Contains(e.ToNodeId))
            .ToList();
    }

    // ==================== 边占用管理 ====================

    /// <summary>
    /// 标记边的占用状态。
    /// </summary>
    public bool MarkEdgeOccupied(string edgeId, bool occupied)
    {
        while (true)
        {
            if (!_edges.TryGetValue(edgeId, out var edge))
                return false;

            // 状态未变则跳过
            if (edge.IsOccupied == occupied)
                return true;

            var updated = edge with { IsOccupied = occupied };
            if (_edges.TryUpdate(edgeId, updated, edge))
                return true;

            // 并发冲突，重试
        }
    }

    /// <summary>
    /// 批量标记边的占用状态。
    /// </summary>
    public void MarkEdgesOccupied(IEnumerable<string> edgeIds, bool occupied)
    {
        foreach (var edgeId in edgeIds)
        {
            MarkEdgeOccupied(edgeId, occupied);
        }
    }

    /// <summary>
    /// 获取所有已占用的边。
    /// </summary>
    public IReadOnlyList<Edge> GetOccupiedEdges()
    {
        return _edges.Values.Where(e => e.IsOccupied).ToList();
    }

    // ==================== 快照支持 ====================

    /// <summary>
    /// 获取当前拓扑图的完整快照。
    /// </summary>
    public TopologySnapshot GetSnapshot()
    {
        return new TopologySnapshot
        {
            Zones = new Dictionary<string, Zone>(_zones),
            Nodes = new Dictionary<string, Node>(_nodes),
            Edges = new Dictionary<string, Edge>(_edges)
        };
    }

    /// <summary>
    /// 从快照恢复拓扑图。注意：会清空当前所有数据。
    /// </summary>
    public void RestoreFromSnapshot(TopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 清空现有数据
        _zones.Clear();
        _nodes.Clear();
        _edges.Clear();
        _outgoingEdges.Clear();
        _incomingEdges.Clear();

        // 恢复区域
        foreach (var kvp in snapshot.Zones)
        {
            _zones[kvp.Key] = kvp.Value;
        }

        // 恢复节点（同时重建邻接表）
        foreach (var kvp in snapshot.Nodes)
        {
            _nodes[kvp.Key] = kvp.Value;
            _outgoingEdges.GetOrAdd(kvp.Key, _ => new HashSet<string>());
            _incomingEdges.GetOrAdd(kvp.Key, _ => new HashSet<string>());
        }

        // 恢复边（同时重建邻接关系）
        foreach (var kvp in snapshot.Edges)
        {
            _edges[kvp.Key] = kvp.Value;
            var edge = kvp.Value;

            if (!string.IsNullOrEmpty(edge.FromNodeId))
            {
                var outgoing = _outgoingEdges.GetOrAdd(edge.FromNodeId, _ => new HashSet<string>());
                lock (_adjacencyLock) { outgoing.Add(edge.EdgeId); }
            }

            if (!string.IsNullOrEmpty(edge.ToNodeId))
            {
                var incoming = _incomingEdges.GetOrAdd(edge.ToNodeId, _ => new HashSet<string>());
                lock (_adjacencyLock) { incoming.Add(edge.EdgeId); }
            }
        }
    }

    // ==================== 路径验证 ====================

    /// <summary>
    /// 验证一条路径（按节点序列）是否有效，即每对连续节点之间存在有向边。
    /// </summary>
    public bool ValidatePath(IReadOnlyList<string> nodePath)
    {
        if (nodePath == null || nodePath.Count < 2)
            return false;

        for (int i = 0; i < nodePath.Count - 1; i++)
        {
            var from = nodePath[i];
            var to = nodePath[i + 1];

            var outgoing = GetOutgoingEdges(from);
            bool hasEdge = outgoing.Any(e => e.ToNodeId == to && !e.IsOccupied);

            if (!hasEdge)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 获取拓扑图统计信息。
    /// </summary>
    public TopologyStats GetStats()
    {
        return new TopologyStats
        {
            ZoneCount = _zones.Count,
            NodeCount = _nodes.Count,
            EdgeCount = _edges.Count,
            OccupiedEdgeCount = _edges.Values.Count(e => e.IsOccupied)
        };
    }

    /// <summary>
    /// 清空拓扑图所有数据。
    /// </summary>
    public void Clear()
    {
        _zones.Clear();
        _nodes.Clear();
        _edges.Clear();
        _outgoingEdges.Clear();
        _incomingEdges.Clear();
    }

    // ==================== 内部方法 ====================

    /// <summary>
    /// 从 predecessor 字典回溯重建路径。
    /// </summary>
    private static PathResult ReconstructPath(
        string fromNodeId,
        string toNodeId,
        Dictionary<string, (string NodeId, string EdgeId)> predecessor,
        int totalWeight)
    {
        var nodePath = new List<string>();
        var edgePath = new List<string>();

        var current = toNodeId;
        while (current != fromNodeId)
        {
            nodePath.Add(current);
            if (predecessor.TryGetValue(current, out var pred))
            {
                edgePath.Add(pred.EdgeId);
                current = pred.NodeId;
            }
            else
            {
                // 不应发生 — 但防御性处理
                return PathResult.NotFound;
            }
        }

        nodePath.Add(fromNodeId);
        nodePath.Reverse();
        edgePath.Reverse();

        return new PathResult
        {
            Found = true,
            NodePath = nodePath,
            EdgePath = edgePath,
            TotalWeight = totalWeight
        };
    }
}

/// <summary>
/// 拓扑图统计信息。
/// </summary>
public record TopologyStats
{
    public int ZoneCount { get; init; }
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public int OccupiedEdgeCount { get; init; }
}
