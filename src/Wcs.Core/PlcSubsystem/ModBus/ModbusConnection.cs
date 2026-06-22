using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Interfaces;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.Modbus;

/// <summary>
/// Modbus TCP 连接配置
/// </summary>
public class ModbusConnectionConfig
{
    public string Name { get; set; } = "ModbusPLC";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;
    public int TimeoutMs { get; set; } = 5000;
    public int RetryCount { get; set; } = 3;
}

/// <summary>
/// Modbus TCP 连接实现 — 基于 NModbus 库
/// </summary>
public class ModbusConnection : PlcConnectionBase
{
    private readonly ModbusConnectionConfig _config;
    private readonly ModbusFactory _factory = new();
    private System.Net.Sockets.TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public override PlcProtocolType ProtocolType => PlcProtocolType.Modbus;
    public IModbusMaster? Master => _master;

    public ModbusConnection(ModbusConnectionConfig config, ILogger<ModbusConnection> logger)
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
            _tcpClient = new System.Net.Sockets.TcpClient();
            await _tcpClient.ConnectAsync(_config.Host, _config.Port, ct);
            _master = _factory.CreateMaster(_tcpClient);
            Connected = true;
            SetStatus(PlcConnectionStatusEnum.Connected);
            Logger.LogInformation("Modbus [{Name}] connected to {Host}:{Port}",
                Name, _config.Host, _config.Port);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            Logger.LogError(ex, "Modbus [{Name}] connect failed", Name);
            return false;
        }
    }

    public override async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        if (!Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Disconnecting);

        try
        {
            _master?.Dispose();
            _tcpClient?.Close();
            Connected = false;
            SetStatus(PlcConnectionStatusEnum.Disconnected);
            Logger.LogInformation("Modbus [{Name}] disconnected", Name);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            return false;
        }
    }

    public override Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (_master == null || !Connected)
            return Task.FromResult<byte[]?>(null);

        try
        {
            var parts = address.Split(':');
            var type = parts[0].ToUpperInvariant();
            var start = ushort.Parse(parts[1]);

            ushort[] values;
            switch (type)
            {
                case "HR":
                case "HOLDING":
                    values = _master.ReadHoldingRegistersAsync(_config.UnitId, start, length)
                        .GetAwaiter().GetResult();
                    break;
                case "IR":
                case "INPUT":
                    values = _master.ReadInputRegistersAsync(_config.UnitId, start, length)
                        .GetAwaiter().GetResult();
                    break;
                default:
                    return Task.FromResult<byte[]?>(null);
            }

            CountRead();
            var result = new byte[length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                result[i * 2] = (byte)(values[i] >> 8);
                result[i * 2 + 1] = (byte)(values[i] & 0xFF);
            }
            return Task.FromResult<byte[]?>(result);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Modbus [{Name}] read {Addr} failed", Name, address);
            return Task.FromResult<byte[]?>(null);
        }
    }

    public override async Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        if (_master == null || !Connected) return false;

        try
        {
            var parts = address.Split(':');
            var type = parts[0].ToUpperInvariant();
            var start = ushort.Parse(parts[1]);

            var values = new ushort[data.Length / 2 + (data.Length % 2)];
            Buffer.BlockCopy(data, 0, values, 0, data.Length);

            switch (type)
            {
                case "HR":
                case "HOLDING":
                    await _master.WriteMultipleRegistersAsync(_config.UnitId, start, values);
                    break;
                case "COIL":
                    await _master.WriteSingleCoilAsync(_config.UnitId, start, data[0] != 0);
                    break;
                default:
                    return false;
            }

            CountWrite();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Modbus [{Name}] write {Addr} failed", Name, address);
            return false;
        }
    }

    public override void Dispose()
    {
        _master?.Dispose();
        _tcpClient?.Dispose();
        base.Dispose();
    }
}
