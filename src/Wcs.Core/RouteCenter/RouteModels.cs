namespace Wcs.Core.RouteCenter;

using Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 路段拥塞状态
/// </summary>
public enum CongestionLevel
{
    /// <summary>畅通</summary>
    Clear = 0,
    /// <summary>轻度拥塞</summary>
    Light = 1,
    /// <summary>中度拥塞</summary>
    Moderate = 2,
    /// <summary>严重拥塞</summary>
    Heavy = 3
}

/// <summary>
/// 路由请求 — 描述从 A 到 B 的路径需求
/// </summary>
public class RouteRequest
{
    /// <summary>请求 ID</summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>起点节点 ID</summary>
    public string FromNodeId { get; set; } = string.Empty;

    /// <summary>终点节点 ID</summary>
    public string ToNodeId { get; set; } = string.Empty;

    /// <summary>请求方物体 ID</summary>
    public string? ObjectId { get; set; }

    /// <summary>所需能力（可选）</summary>
    public EdgeCapability? RequiredCapability { get; set; }

    /// <summary>路径策略：最短/最空/平衡</summary>
    public RouteStrategy Strategy { get; set; } = RouteStrategy.Shortest;

    /// <summary>请求优先级（越大约优先）</summary>
    public int Priority { get; set; } = 0;
}

/// <summary>
/// 路径策略
/// </summary>
public enum RouteStrategy
{
    /// <summary>最短路径（默认）</summary>
    Shortest = 0,
    /// <summary>最空路径（避开拥塞）</summary>
    LeastCongested = 1,
    /// <summary>平衡模式</summary>
    Balanced = 2
}

/// <summary>
/// 路由结果
/// </summary>
public class RouteResult
{
    /// <summary>是否找到路径</summary>
    public bool Found { get; set; }

    /// <summary>路径节点列表</summary>
    public IReadOnlyList<string> NodePath { get; set; } = Array.Empty<string>();

    /// <summary>路径边列表</summary>
    public IReadOnlyList<string> EdgePath { get; set; } = Array.Empty<string>();

    /// <summary>路径总权重</summary>
    public int TotalWeight { get; set; }

    /// <summary>路径拥塞级别</summary>
    public CongestionLevel Congestion { get; set; } = CongestionLevel.Clear;

    /// <summary>绕过的故障节点</summary>
    public IReadOnlyList<string>? BypassedNodes { get; set; }

    /// <summary>未找到路径时的原因</summary>
    public string? FailureReason { get; set; }

    public static RouteResult NotFound(string reason = "") =>
        new() { Found = false, FailureReason = reason };
}

/// <summary>
/// 路段拥塞记录
/// </summary>
public class CongestionRecord
{
    /// <summary>边 ID</summary>
    public string EdgeId { get; set; } = string.Empty;

    /// <summary>当前占用数</summary>
    public int OccupiedCount { get; set; }

    /// <summary>拥塞级别</summary>
    public CongestionLevel Level { get; set; }

    /// <summary>最近更新时间</summary>
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 动态路由中心接口 — 寻路、避障、拥塞控制
/// </summary>
public interface IRouteCenter
{
    /// <summary>
    /// 计算最优路径
    /// </summary>
    RouteResult FindRoute(RouteRequest request);

    /// <summary>
    /// 标记路径占用的边
    /// </summary>
    void OccupyPath(IReadOnlyList<string> edgeIds, string? objectId);

    /// <summary>
    /// 释放路径占用的边
    /// </summary>
    void ReleasePath(IReadOnlyList<string> edgeIds, string? objectId);

    /// <summary>
    /// 标记节点故障（避开该节点）
    /// </summary>
    void MarkNodeFault(string nodeId, bool isFaulted);

    /// <summary>
    /// 标记边故障
    /// </summary>
    void MarkEdgeFault(string edgeId, bool isFaulted);

    /// <summary>
    /// 获取拥塞状态
    /// </summary>
    CongestionLevel GetCongestion(string edgeId);

    /// <summary>
    /// 获取拥塞报告
    /// </summary>
    IReadOnlyList<CongestionRecord> GetCongestionReport();

    /// <summary>
    /// 清空所有动态状态
    /// </summary>
    void Reset();

    /// <summary>
    /// 统计信息
    /// </summary>
    RouteCenterStats GetStats();
}

/// <summary>
/// 路由中心统计
/// </summary>
public class RouteCenterStats
{
    public double AvgPathLength { get; set; }
    public int TotalRoutesCalculated { get; set; }
    public int TotalRouteFailures { get; set; }
    public int FaultedNodes { get; set; }
    public int FaultedEdges { get; set; }
    public int CongestedEdges { get; set; }
}
