namespace Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>
/// PLC 连接统一接口 — 所有协议（Modbus / OPC UA / EIP / S7 / MQTT）均实现此接口
/// </summary>
public interface IPlcConnection : IDisposable
{
    /// <summary>连接名称（对应配置中的标识）</summary>
    string Name { get; }

    /// <summary>协议类型</summary>
    PlcProtocolType ProtocolType { get; }

    /// <summary>是否已连接</summary>
    bool IsConnected { get; }

    /// <summary>连接</summary>
    Task<bool> ConnectAsync(CancellationToken ct = default);

    /// <summary>断开</summary>
    Task<bool> DisconnectAsync(CancellationToken ct = default);

    /// <summary>读取字节数据</summary>
    Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default);

    /// <summary>写入字节数据</summary>
    Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default);

    /// <summary>获取当前连接状态</summary>
    PlcConnectionStatus GetStatus();
}
