namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// OPC UA 块标记 — 标注类对应的 OPC UA 节点分组
///
/// 用法：
///   [PlcOpcUaBlock]
///   public class ConveyorStatus
///   {
///       [PlcOpcUaTag("ns=2;s=CV01.Speed")] public short Speed { get; set; }
///       [PlcOpcUaTag("ns=2;s=CV01.Running")] public bool Running { get; set; }
///   }
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class PlcOpcUaBlockAttribute : Attribute
{
    /// <summary>轮询间隔（毫秒）</summary>
    public int RefreshRateMs { get; init; } = 1000;
    /// <summary>超时（毫秒）</summary>
    public int TimeoutMs { get; init; } = 3000;
}

/// <summary>
/// OPC UA 节点标记 — 标注属性对应的 OPC UA 节点 ID
///
/// 节点 ID 格式（与 OPC UA 规范一致）：
///   - "ns=2;s=MyVariable"   → 数字命名空间 + 字符串标识
///   - "ns=0;i=85"           → 数字命名空间 + 数字索引
///   - "ns=3;g=..."          → 数字命名空间 + GUID
///   - "b=..."               → 字节串标识
///
/// 示例：
///   [PlcOpcUaTag("ns=2;s=PLC1.CV01.DriveReady")]
///   public bool DriveReady { get; set; }
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class PlcOpcUaTagAttribute : Attribute
{
    /// <summary>OPC UA 节点 ID 字符串</summary>
    public string NodeId { get; }

    public PlcOpcUaTagAttribute(string nodeId)
    {
        NodeId = nodeId;
    }
}
