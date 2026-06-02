using Wcs.Core.ObjectTracking.Topology;

namespace WcsCoreTests;

/// <summary>
/// TopologyGraph 测试：Zone/Node/Edge CRUD、BFS 最短路径、占用管理
/// </summary>
public class TopologyGraphTests
{
    private static TopologyGraph CreateSimpleGraph()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "Z1", DisplayName = "Storage Zone" });

        for (int i = 1; i <= 5; i++)
        {
            g.AddNode(new Node { NodeId = $"N{i}", ZoneId = "Z1" });
        }

        // Linear path: N1 → N2 → N3 → N4 → N5
        g.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "N2", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E2", FromNodeId = "N2", ToNodeId = "N3", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E3", FromNodeId = "N3", ToNodeId = "N4", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E4", FromNodeId = "N4", ToNodeId = "N5", Weight = 1 });

        return g;
    }

    // ========== CRUD ==========

    [Fact]
    public void AddAndGetNode_RoundTrip()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "Z1" });
        g.AddNode(new Node { NodeId = "N1", ZoneId = "Z1" });
        Assert.NotNull(g.GetNode("N1"));
    }

    [Fact]
    public void AddNode_DuplicateId_ReturnsFalse()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "Z1" });
        Assert.True(g.AddNode(new Node { NodeId = "N1", ZoneId = "Z1" }));
        Assert.False(g.AddNode(new Node { NodeId = "N1", ZoneId = "Z1" }));
    }

    [Fact]
    public void RemoveNode_RemovesAssociatedEdges()
    {
        var g = CreateSimpleGraph();
        g.RemoveNode("N3");

        Assert.Null(g.GetNode("N3"));
        // Edges E2 and E3 should also be gone
        Assert.Null(g.GetEdge("E2"));
        Assert.Null(g.GetEdge("E3"));
        // E1 and E4 should remain
        Assert.NotNull(g.GetEdge("E1"));
        Assert.NotNull(g.GetEdge("E4"));
    }

    [Fact]
    public void GetNode_Unknown_ReturnsNull()
    {
        var g = new TopologyGraph();
        Assert.Null(g.GetNode("NONEXISTENT"));
    }

    [Fact]
    public void HasNode_ReturnsCorrect()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "Z1" });
        g.AddNode(new Node { NodeId = "N1", ZoneId = "Z1" });
        Assert.True(g.HasNode("N1"));
        Assert.False(g.HasNode("N2"));
    }

    [Fact]
    public void AddEdge_UnknownFromNode_ReturnsFalse()
    {
        var g = new TopologyGraph();
        g.AddNode(new Node { NodeId = "N1", ZoneId = "Z1" });
        Assert.False(g.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "NONEXISTENT" }));
    }

    // ========== BFS Shortest Path ==========

    [Fact]
    public void GetShortestPath_LinearGraph_FindsPath()
    {
        var g = CreateSimpleGraph();
        var path = g.GetShortestPath("N1", "N5");

        Assert.True(path.Found);
        Assert.Equal(new[] { "N1", "N2", "N3", "N4", "N5" }, path.NodePath);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4" }, path.EdgePath);
    }

    [Fact]
    public void GetShortestPath_SameNode_ReturnsEmptyPath()
    {
        var g = CreateSimpleGraph();
        var path = g.GetShortestPath("N1", "N1");

        Assert.True(path.Found);
        Assert.Single(path.NodePath);
        Assert.Empty(path.EdgePath);
        Assert.Equal(0, path.TotalWeight);
    }

    [Fact]
    public void GetShortestPath_Unreachable_ReturnsNotFound()
    {
        var g = CreateSimpleGraph();
        var path = g.GetShortestPath("N5", "N1"); // graph is directed one-way
        Assert.False(path.Found);
    }

    [Fact]
    public void GetShortestPath_UnknownNode_ReturnsNotFound()
    {
        var g = CreateSimpleGraph();
        var path = g.GetShortestPath("N1", "NONEXISTENT");
        Assert.False(path.Found);
    }

    [Fact]
    public void GetShortestPath_AvoidsOccupiedEdges()
    {
        var g = CreateSimpleGraph();
        g.MarkEdgeOccupied("E2", true); // N2→N3 is blocked

        var path = g.GetShortestPath("N1", "N5");

        // Since it's a linear graph with E2 occupied, N5 should be unreachable
        Assert.False(path.Found);
    }

    [Fact]
    public void GetShortestPath_WithCapability_FiltersEdges()
    {
        var g = new TopologyGraph();
        g.AddNode(new Node { NodeId = "N1" });
        g.AddNode(new Node { NodeId = "N2" });
        g.AddNode(new Node { NodeId = "N3" });

        // E1 supports Both (includes Transfer), E2 only Transport
        g.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "N2", Weight = 1, Capability = EdgeCapability.Both });
        g.AddEdge(new Edge { EdgeId = "E2", FromNodeId = "N2", ToNodeId = "N3", Weight = 1, Capability = EdgeCapability.Both });

        var path = g.GetShortestPath("N1", "N3", EdgeCapability.Transfer);
        Assert.True(path.Found);
    }

    // ========== Graph with branch ==========

    private static TopologyGraph CreateBranchGraph()
    {
        var g = new TopologyGraph();
        // N1 → N2 → N3 (short path)
        // N1 → N4 → N5 → N3 (long path)
        for (int i = 1; i <= 5; i++)
            g.AddNode(new Node { NodeId = $"N{i}" });

        g.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "N2", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E2", FromNodeId = "N2", ToNodeId = "N3", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E3", FromNodeId = "N1", ToNodeId = "N4", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E4", FromNodeId = "N4", ToNodeId = "N5", Weight = 1 });
        g.AddEdge(new Edge { EdgeId = "E5", FromNodeId = "N5", ToNodeId = "N3", Weight = 1 });

        return g;
    }

    [Fact]
    public void GetShortestPath_BranchGraph_FindsShortestPath()
    {
        var g = CreateBranchGraph();
        var path = g.GetShortestPath("N1", "N3");

        Assert.True(path.Found);
        // Should take N1→N2→N3 (2 hops) not N1→N4→N5→N3 (3 hops)
        Assert.Equal(new[] { "N1", "N2", "N3" }, path.NodePath);
    }

    [Fact]
    public void GetShortestPath_ShortPathBlocked_TakesAlternative()
    {
        var g = CreateBranchGraph();
        g.MarkEdgeOccupied("E1", true); // N1→N2 blocked

        var path = g.GetShortestPath("N1", "N3");

        Assert.True(path.Found);
        // Should take N1→N4→N5→N3 (longer route)
        Assert.Equal(new[] { "N1", "N4", "N5", "N3" }, path.NodePath);
    }

    // ========== Reachability ==========

    [Fact]
    public void IsReachable_LinearGraph_ReturnsCorrect()
    {
        var g = CreateSimpleGraph();
        Assert.True(g.IsReachable("N1", "N5"));
        Assert.False(g.IsReachable("N5", "N1"));
    }

    [Fact]
    public void GetReachableNodes_ReturnsAllReachable()
    {
        var g = CreateSimpleGraph();
        var reachable = g.GetReachableNodes("N1");
        Assert.Equal(4, reachable.Count);
        Assert.Contains("N2", reachable);
        Assert.Contains("N5", reachable);
    }

    // ========== Occupancy ==========

    [Fact]
    public void MarkEdgeOccupied_Toggle_Succeeds()
    {
        var g = CreateSimpleGraph();
        Assert.True(g.MarkEdgeOccupied("E1", true));
        Assert.True(g.GetEdge("E1")!.IsOccupied);
        Assert.True(g.MarkEdgeOccupied("E1", false));
        Assert.False(g.GetEdge("E1")!.IsOccupied);
    }

    [Fact]
    public void GetOccupiedEdges_ReturnsOnlyOccupied()
    {
        var g = CreateSimpleGraph();
        g.MarkEdgeOccupied("E2", true);
        g.MarkEdgeOccupied("E4", true);

        var occupied = g.GetOccupiedEdges().ToList();
        Assert.Equal(2, occupied.Count);
        Assert.Contains(occupied, e => e.EdgeId == "E2");
        Assert.Contains(occupied, e => e.EdgeId == "E4");
    }

    // ========== Zone operations ==========

    [Fact]
    public void AddZone_And_GetZoneNodes()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "ZA", DisplayName = "Zone A" });
        g.AddNode(new Node { NodeId = "N1", ZoneId = "ZA" });
        g.AddNode(new Node { NodeId = "N2", ZoneId = "ZA" });

        var nodes = g.GetZoneNodes("ZA");
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public void RemoveZone_RemovesAllNodes()
    {
        var g = new TopologyGraph();
        g.AddZone(new Zone { ZoneId = "ZA" });
        g.AddNode(new Node { NodeId = "N1", ZoneId = "ZA" });

        g.RemoveZone("ZA");
        Assert.Null(g.GetZone("ZA"));
        Assert.Null(g.GetNode("N1"));
    }

    // ========== Snapshot ==========

    [Fact]
    public void Snapshot_RoundTrip()
    {
        var g = CreateSimpleGraph();
        var snap = g.GetSnapshot();

        var g2 = new TopologyGraph();
        g2.RestoreFromSnapshot(snap);

        Assert.NotNull(g2.GetNode("N1"));
        Assert.NotNull(g2.GetEdge("E1"));

        var path = g2.GetShortestPath("N1", "N5");
        Assert.True(path.Found);
    }

    // ========== Stats ==========

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var g = CreateSimpleGraph();
        g.MarkEdgeOccupied("E2", true);

        var stats = g.GetStats();
        Assert.Equal(1, stats.ZoneCount);
        Assert.Equal(5, stats.NodeCount);
        Assert.Equal(4, stats.EdgeCount);
        Assert.Equal(1, stats.OccupiedEdgeCount);
    }

    // ========== Clear ==========

    [Fact]
    public void Clear_RemovesAll()
    {
        var g = CreateSimpleGraph();
        g.Clear();
        Assert.Equal(0, g.GetStats().NodeCount);
    }
}
