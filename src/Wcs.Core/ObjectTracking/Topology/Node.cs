namespace Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 节点类型
/// </summary>
public enum NodeType
{
    TransferPoint,  // 转运点
    Buffer,         // 缓冲位
    Junction,       // 汇合点
    DivergePoint,   // 分岔点
    EntryPoint,     // 入口
    ExitPoint       // 出口
}

/// <summary>
/// 拓扑节点 — 输送线中的一个可遍历点
/// </summary>
public record Node
{
    public string NodeId { get; init; } = string.Empty;
    public string ZoneId { get; init; } = string.Empty;
    public string ConveyorId { get; init; } = string.Empty;
    public string PositionId { get; init; } = string.Empty;
    public NodeType Type { get; init; } = NodeType.TransferPoint;
    public Dictionary<string, object> Attributes { get; init; } = new();
}
