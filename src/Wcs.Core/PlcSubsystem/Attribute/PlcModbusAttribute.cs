namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// Modbus 块标记 — 标注类对应的 Modbus 寄存器区域和设备
///
/// 用法：
///   [PlcModbusBlock("HR", UnitId = 1)]
///   public class ConveyorCommand
///   {
///       [PlcModbusTag(0)] public short Speed;         // HR0
///       [PlcModbusTag(0, Bit = 0)] public bool Start; // HR0.0
///   }
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class PlcModbusBlockAttribute : Attribute
{
    /// <summary>寄存器类型：HR(保持寄存器)、IR(输入寄存器)、COIL(线圈)</summary>
    public string RegisterType { get; }
    /// <summary>Modbus 从站地址</summary>
    public byte UnitId { get; set; } = 1;
    /// <summary>轮询间隔（毫秒）</summary>
    public int RefreshRateMs { get; init; } = 1000;
    /// <summary>超时（毫秒）</summary>
    public int TimeoutMs { get; init; } = 3000;

    public PlcModbusBlockAttribute(string registerType)
    {
        RegisterType = registerType;
    }
}

/// <summary>
/// Modbus 寄存器标记 — 标注属性对应的寄存器偏移
///
/// 地址格式：{RegisterType}:{Offset}
/// 示例：HR:0 → 保持寄存器 0, COIL:0 → 线圈 0
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class PlcModbusTagAttribute : Attribute
{
    /// <summary>寄存器偏移</summary>
    public int Offset { get; }
    /// <summary>位偏移（-1=整个寄存器，0~15=指定位）</summary>
    public int Bit { get; init; } = -1;

    public PlcModbusTagAttribute(int offset)
    {
        Offset = offset;
    }
}
