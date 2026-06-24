namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 块标记 — 标注 struct 所属的 PLC 名称和 DB 块号
///
/// 让命令/状态结构体自描述：我是写给哪个 PLC 的哪个 DB 块的。
/// 不再需要外部映射配置。
///
/// 用法：
///   [PlcBlock("PLC1", 101)]
///   public struct ConveyorCommand
///   {
///       [PlcOffset(0, 0)] public bool Start;
///       [PlcOffset(2)] public short Speed;
///   }
///
///   之后 PlcWriter 或 CommandCenter 直接通过特性知道写到哪里。
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public class PlcBlockAttribute : Attribute
{
    /// <summary>PLC 名称（对应 PlcConnections 中的 PlcName）</summary>
    public string PlcName { get; }

    /// <summary>DB 块号</summary>
    public int DbBlock { get; }

    /// <summary>起始字节偏移（默认 0）</summary>
    public int StartByte { get; set; }

     public int Length { get; set; }

    /// <param name="plcName">PLC 名称</param>
    /// <param name="dbBlock">DB 块号</param>
    public PlcBlockAttribute(string plcName, int dbBlock)
    {
        PlcName = plcName ?? throw new ArgumentNullException(nameof(plcName));
        DbBlock = dbBlock;
    }

      public PlcBlockAttribute(string plcName, int dbBlock,int startByte,int length)
    {
        PlcName = plcName ?? throw new ArgumentNullException(nameof(plcName));
        DbBlock = dbBlock;
        StartByte = startByte;
        Length = length;
    }
}
