namespace Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 边能力标志
/// </summary>
[Flags]
public enum EdgeCapability
{
    None = 0,
    Transport = 1,
    Transfer = 2,
    Both = Transport | Transfer
}

/// <summary>
/// 拓扑边 — 两个节点间的有向连接
/// </summary>
public record Edge
{
    public string EdgeId { get; init; } = string.Empty;
    public string FromNodeId { get; init; } = string.Empty;
    public string ToNodeId { get; init; } = string.Empty;
    public int Weight { get; init; } = 1;
    public bool IsOccupied { get; set; }
    public EdgeCapability Capability { get; init; } = EdgeCapability.Transport;
}
