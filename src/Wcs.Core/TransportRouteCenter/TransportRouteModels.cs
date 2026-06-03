namespace Wcs.Core.RouteCenter;

using Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 路段拥塞状态
/// </summary>
public enum CongestionLevel
{
    Clear = 0,
    Light = 1,
    Moderate = 2,
    Heavy = 3
}

/// <summary>
/// 运输路由请求 — 从设备 A 到设备 B 的路径需求
/// </summary>
public class TransportRouteRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string? ObjectId { get; set; }
    public EdgeCapability? RequiredCapability { get; set; }
    public TransportRouteStrategy Strategy { get; set; } = TransportRouteStrategy.Shortest;
    public int Priority { get; set; } = 0;
}

public enum TransportRouteStrategy
{
    Shortest = 0,
    LeastCongested = 1,
    Balanced = 2
}

public class TransportRouteResult
{
    public bool Found { get; set; }
    public IReadOnlyList<string> NodePath { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> EdgePath { get; set; } = Array.Empty<string>();
    public int TotalWeight { get; set; }
    public CongestionLevel Congestion { get; set; } = CongestionLevel.Clear;
    public IReadOnlyList<string>? BypassedNodes { get; set; }
    public string? FailureReason { get; set; }

    public static TransportRouteResult NotFound(string reason = "") =>
        new() { Found = false, FailureReason = reason };
}

public class CongestionRecord
{
    public string EdgeId { get; set; } = string.Empty;
    public int OccupiedCount { get; set; }
    public CongestionLevel Level { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 运输路由中心 — 设备级路径规划、避障、拥塞控制
///
/// 纯 WCS 职责：只做「从设备 A 到设备 B」的路径规划
/// 不做：库位选择、FIFO、库存分配（这些属于 WMS）
/// </summary>
public interface ITransportRouteCenter
{
    TransportRouteResult FindRoute(TransportRouteRequest request);
    void OccupyPath(IReadOnlyList<string> edgeIds, string? objectId);
    void ReleasePath(IReadOnlyList<string> edgeIds, string? objectId);
    void MarkNodeFault(string nodeId, bool isFaulted);
    void MarkEdgeFault(string edgeId, bool isFaulted);
    CongestionLevel GetCongestion(string edgeId);
    IReadOnlyList<CongestionRecord> GetCongestionReport();
    void Reset();
    TransportRouteStats GetStats();
}

public class TransportRouteStats
{
    public int TotalRoutesCalculated { get; set; }
    public int TotalRouteFailures { get; set; }
    public int FaultedNodes { get; set; }
    public int FaultedEdges { get; set; }
    public int CongestedEdges { get; set; }
}
