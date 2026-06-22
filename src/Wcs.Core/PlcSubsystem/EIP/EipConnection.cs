using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Text;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.Eip;

/// <summary>
/// EtherNet/IP 连接配置
/// </summary>
public class EipConnectionConfig
{
    public string Name { get; set; } = "EipPLC";
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 44818;
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>
/// EtherNet/IP 连接实现 — 基于 TCP Socket 的 CIP 协议
/// 正式生产建议使用 libplctag 库（需安装原生运行时）
/// </summary>
public class EipConnection : PlcConnectionBase
{
    private readonly EipConnectionConfig _config;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public override PlcProtocolType ProtocolType => PlcProtocolType.EIP;

    public EipConnection(EipConnectionConfig config, ILogger<EipConnection> logger)
        : base(config.Name, logger)
    {
        _config = config;
        Status.ProtocolType = ProtocolType;
    }

    public override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Connecting);

        try
        {
            _tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(_config.TimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await _tcpClient.ConnectAsync(_config.Host, _config.Port, linkedCts.Token);
            _stream = _tcpClient.GetStream();

            Connected = true;
            SetStatus(PlcConnectionStatusEnum.Connected);
            Logger.LogInformation("EIP [{Name}] connected to {Host}:{Port}",
                Name, _config.Host, _config.Port);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            Logger.LogError(ex, "EIP [{Name}] connect failed", Name);
            return false;
        }
    }

    public override async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        if (!Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Disconnecting);

        try
        {
            _stream?.Close();
            _tcpClient?.Close();
            Connected = false;
            SetStatus(PlcConnectionStatusEnum.Disconnected);
            Logger.LogInformation("EIP [{Name}] disconnected", Name);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            return false;
        }
    }

    public override async Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (_stream == null || !Connected) return null;

        try
        {
            // CIP 读取请求 — 简化实现，生产环境需构造完整 CIP 报文
            var buffer = new byte[length];
            var read = await _stream.ReadAsync(buffer, 0, length, ct);
            if (read > 0) CountRead();
            return read > 0 ? buffer[..read] : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "EIP [{Name}] read {Addr} failed", Name, address);
            return null;
        }
    }

    public override async Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        if (_stream == null || !Connected) return false;

        try
        {
            await _stream.WriteAsync(data, ct);
            CountWrite();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "EIP [{Name}] write {Addr} failed", Name, address);
            return false;
        }
    }

    public override void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
        base.Dispose();
    }
}
