namespace Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>
/// PLC 标签式客户端接口 — 按标签名读写 PLC 变量
///
/// 与 IPlcConnection（按地址字符串读写字节）不同，IPlcClient 工作在
/// "标签"语义层：调用方只关心标签名和数据值，不关心协议细节。
///
/// 实现方可选择底层协议：
///   - Snap7PlcClient：通过 PlcTagRegistry 将标签名解析为(DB,偏移,类型)，
///     再调用 ReadPool/WritePool 的 Snap7 通信
///   - 未来也可实现基于 S7-1500 Plus 协议的符号标签客户端
/// </summary>
public interface IPlcClient
{
    /// <summary>读取单个标签的值</summary>
    /// <param name="tagName">标签名（如 "DB1.CV01_DriveReady"）</param>
    /// <param name="timeoutMs">超时毫秒</param>
    /// <returns>标签值（已转为目标类型），失败返回 null</returns>
    Task<object?> ReadAsync(string tagName, int timeoutMs = 3000);

    /// <summary>写入单个标签的值</summary>
    Task WriteAsync(string tagName, object? value, int timeoutMs = 3000);

    /// <summary>批量读取多个标签（可优化为单次 DB 块读取）</summary>
    Task<object?[]> ReadBatchAsync(string[] tagNames, int timeoutMs = 3000);

    /// <summary>批量写入多个标签（可合并为单次 DB 块写入）</summary>
    Task WriteBatchAsync(IEnumerable<(string Name, object? Value)> writes, int timeoutMs = 3000);
}
