namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 偏移标记 — 标注结构体字段对应的 PLC DB 块偏移
///
/// 用于命令/状态结构体，告知序列化器字段在 byte[] 中的位置。
///
/// 示例：
///   public class LiftCommand
///   {
///       [PlcOffset(0)] public bool Start { get; set; }       // byte[0] bit 0
///       [PlcOffset(0, 1)] public bool DirectionUp { get; set; } // byte[0] bit 1
///       [PlcOffset(2)] public short TargetFloor { get; set; } // byte[2..3]
///   }
///
/// 序列化器自动按偏移组装 byte[]。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class PlcOffsetAttribute : Attribute
{
    /// <summary>字节偏移</summary>
    public int ByteOffset { get; }
    /// <summary>位偏移（-1=整个字节，0~7=特定位）</summary>
    public int BitOffset { get; } = -1;

    /// <param name="byteOffset">字节偏移</param>
    /// <param name="bitOffset">位偏移（默认-1=整个字节）</param>
    public PlcOffsetAttribute(int byteOffset, int bitOffset = -1)
    {
        ByteOffset = byteOffset;
        BitOffset = bitOffset;
    }
}
