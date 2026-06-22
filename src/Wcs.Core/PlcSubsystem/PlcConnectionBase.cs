using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 连接基类 — 统一管理状态、重试、日志
/// </summary>
public abstract class PlcConnectionBase : IPlcConnection
{
    protected readonly ILogger Logger;
    protected readonly PlcConnectionStatus Status = new();
    protected readonly object LockObj = new();
    protected bool Connected;

    public string Name { get; }
    public abstract PlcProtocolType ProtocolType { get; }
    public bool IsConnected => Connected;

    protected PlcConnectionBase(string name, ILogger logger)
    {
        Name = name;
        Logger = logger;
        Status.PlcName = name;
        Status.Status = PlcConnectionStatusEnum.Disconnected;
    }

    public abstract Task<bool> ConnectAsync(CancellationToken ct = default);
    public abstract Task<bool> DisconnectAsync(CancellationToken ct = default);
    public abstract Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default);
    public abstract Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default);

    public virtual void Dispose()
    {
        if (Connected) _ = DisconnectAsync();
    }

    public PlcConnectionStatus GetStatus()
    {
        lock (LockObj)
        {
            return new PlcConnectionStatus
            {
                PlcName = Status.PlcName,
                ProtocolType = Status.ProtocolType,
                Status = Status.Status,
                LastConnectTime = Status.LastConnectTime,
                LastHeartbeat = Status.LastHeartbeat,
                FailureCount = Status.FailureCount,
                LastError = Status.LastError,
                ReadCount = Status.ReadCount,
                WriteCount = Status.WriteCount,
            };
        }
    }

    protected void SetStatus(PlcConnectionStatusEnum s, string? error = null)
    {
        lock (LockObj)
        {
            Status.Status = s;
            if (s == PlcConnectionStatusEnum.Connected)
            {
                Status.LastConnectTime = DateTime.UtcNow;
                Status.FailureCount = 0;
            }
            if (error != null)
            {
                Status.LastError = error;
                if (s == PlcConnectionStatusEnum.Failed)
                    Status.FailureCount++;
            }
        }
    }

    protected void TouchHeartbeat()
    {
        lock (LockObj) Status.LastHeartbeat = DateTime.UtcNow;
    }

    protected void CountRead()
    {
        lock (LockObj) Status.ReadCount++;
        TouchHeartbeat();
    }

    protected void CountWrite()
    {
        lock (LockObj) Status.WriteCount++;
        TouchHeartbeat();
    }
}
