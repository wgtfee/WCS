namespace Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>
/// 标签序列化器接口 — 所有协议（S7CommPlus / Modbus / OPC UA）的序列化器统一签名
///
/// 让 TagWriter 可以接收任意协议的序列化器，无需自动判断协议类型。
/// </summary>
public interface ITagSerializer
{
    /// <summary>从 PLC 读取对象的所有标签属性</summary>
    Task ReadAsync(object obj);

    /// <summary>将对象的所有标签属性写入 PLC</summary>
    Task WriteAsync(object obj);

    /// <summary>检查与 PLC 的连接是否正常</summary>
    Task<bool> CheckHealthAsync();
}
