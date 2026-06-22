namespace Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>
/// PLC 连接状态枚举
/// </summary>
public enum PlcConnectionStatusEnum
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Failed = 3,
    Disconnecting = 4,
}

/// <summary>
/// PLC 连接运行时状态
/// </summary>
public class PlcConnectionStatus
{
    public string PlcName { get; set; } = string.Empty;
    public PlcProtocolType ProtocolType { get; set; }
    public PlcConnectionStatusEnum Status { get; set; }
    public DateTime LastConnectTime { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public long ReadCount { get; set; }
    public long WriteCount { get; set; }
}
