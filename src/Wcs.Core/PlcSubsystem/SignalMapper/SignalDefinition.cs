namespace Wcs.Core.PlcSubsystem.SignalMapper;

/// <summary>
/// PLC 地址 → 业务信号映射定义
/// 将 PLC DB 块中的字节/位偏移映射为业务事件
/// </summary>
public class SignalDefinition
{
    /// <summary>业务信号唯一标识</summary>
    public string SignalId { get; set; } = string.Empty;

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;

    /// <summary>DB 块号</summary>
    public int BlockNumber { get; set; }

    /// <summary>字节偏移</summary>
    public int ByteOffset { get; set; }

    /// <summary>位偏移（-1=整个字节，0-7=特定位）</summary>
    public int BitOffset { get; set; } = -1;

    /// <summary>数据类型：bool/byte/int/word/dword/string</summary>
    public string DataType { get; set; } = "bool";

    /// <summary>目标事件 CLR 类型全名（含命名空间）</summary>
    public string TargetEventType { get; set; } = string.Empty;

    /// <summary>映射到目标事件属性的 JSON 路径表达式</summary>
    public Dictionary<string, string> PropertyMappings { get; set; } = new();

    /// <summary>信号描述</summary>
    public string? Description { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>原始 PLC 地址字符串（如 DB1.DBX355.1）</summary>
    public string PlcAddress =>
        BitOffset >= 0
            ? $"DB{BlockNumber}.DBX{ByteOffset}.{BitOffset}"
            : $"DB{BlockNumber}.DBB{ByteOffset}";
}
