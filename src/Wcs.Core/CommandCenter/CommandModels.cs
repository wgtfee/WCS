namespace Wcs.Core.CommandCenter;

public enum DeviceCommandStatus
{
    Created = 0, Sent = 1, Acked = 2, Executing = 3, Done = 4, Completed = 5,
    Failed = 6, Timeout = 7, Rejected = 8, Cancelled = 9
}

public class DeviceCommandRecord
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");
    public string CommandType { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public DeviceCommandStatus Status { get; set; } = DeviceCommandStatus.Created;
    public string? TaskId { get; set; }
    public string? Payload { get; set; }
    public int TimeoutMs { get; set; } = 5000;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    public DateTime? SentTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public string? Source { get; set; }
}

public interface ICommandCenter
{
    Task<DeviceCommandRecord> SendCommandAsync(string deviceId, string commandType,
        string? payload = null, string? taskId = null, int timeoutMs = 5000, CancellationToken ct = default);

    /// <summary>
    /// 发送带 [PlcBlock] + [PlcOffset] 的结构化命令 struct
    /// 自动从 [PlcBlock("PLC1", 101)] 特性读取目标 PLC/DB 块
    /// </summary>
    Task<DeviceCommandRecord> SendStructuredCommandAsync<T>(string deviceId, string commandType,
        T commandData, string? taskId = null, CancellationToken ct = default) where T : struct;

    /// <summary>
    /// 发送标签命令 — 支持 [PlcStruct]、[PlcModbusBlock]、[PlcOpcUaBlock]
    /// 通过 TagWriter + ITagSerializer 写入，协议无关
    /// </summary>
    Task<DeviceCommandRecord> SendTagCommandAsync<T>(string deviceId, string commandType,
        T commandData, string? taskId = null, CancellationToken ct = default);

    bool ConfirmAcked(string commandId);
    bool ConfirmExecuting(string commandId);
    bool ConfirmDone(string commandId);
    bool ConfirmCompleted(string commandId, string? result = null);
    bool ConfirmFailed(string commandId, string? error = null);
    bool ConfirmTimeout(string commandId);
    bool ConfirmRejected(string commandId, string? reason = null);
    Task<bool> CancelCommandAsync(string commandId, CancellationToken ct = default);
    DeviceCommandRecord? GetCommand(string commandId);
    IEnumerable<DeviceCommandRecord> GetDeviceCommands(string deviceId, int maxCount = 50);
    IEnumerable<DeviceCommandRecord> GetPendingCommands();
    IEnumerable<DeviceCommandRecord> GetTimeoutCommands();
    CommandCenterStats GetStats();
    void Clear();
}

public class CommandCenterStats
{
    public int TotalCommands { get; set; }
    public int CompletedCommands { get; set; }
    public int FailedCommands { get; set; }
    public int TimeoutCommands { get; set; }
    public int PendingCommands { get; set; }
    public double AvgCompletionTimeMs { get; set; }
}
