namespace Wcs.Core.CommandCenter;

/// <summary>
/// 设备命令状态机 — Created → Sent → Accepted → Executing → Completed/Failed/Timeout/Rejected
/// 任务状态 ≠ 设备状态 ≠ 命令状态，三者独立跟踪
/// </summary>
public enum DeviceCommandStatus
{
    /// <summary>已创建</summary>
    Created = 0,
    /// <summary>已发送到 PLC/设备</summary>
    Sent = 1,
    /// <summary>设备已接受</summary>
    Accepted = 2,
    /// <summary>设备执行中</summary>
    Executing = 3,
    /// <summary>执行完成</summary>
    Completed = 4,
    /// <summary>执行失败</summary>
    Failed = 5,
    /// <summary>超时</summary>
    Timeout = 6,
    /// <summary>被设备拒绝</summary>
    Rejected = 7,
    /// <summary>已取消</summary>
    Cancelled = 8
}

/// <summary>
/// 设备命令记录 — 包含完整生命周期和审计信息
/// </summary>
public class DeviceCommandRecord
{
    /// <summary>命令唯一 ID</summary>
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>命令类型（如 StartConveyor, StopLift, ResetRobot）</summary>
    public string CommandType { get; set; } = string.Empty;

    /// <summary>目标设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>当前状态</summary>
    public DeviceCommandStatus Status { get; set; } = DeviceCommandStatus.Created;

    /// <summary>关联的任务 ID</summary>
    public string? TaskId { get; set; }

    /// <summary>命令参数（JSON 序列化）</summary>
    public string? Payload { get; set; }

    /// <summary>超时（毫秒）</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>重试次数</summary>
    public int RetryCount { get; set; }

    /// <summary>最大重试次数</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>发送时间</summary>
    public DateTime? SentTime { get; set; }

    /// <summary>完成时间</summary>
    public DateTime? CompletedTime { get; set; }

    /// <summary>命令来源（模块/组件名）</summary>
    public string? Source { get; set; }
}

/// <summary>
/// 命令中心接口 — 统一管理所有设备命令的生命周期
/// </summary>
public interface ICommandCenter
{
    /// <summary>
    /// 发送命令（跟踪完整生命周期）
    /// </summary>
    Task<DeviceCommandRecord> SendCommandAsync(string deviceId, string commandType,
        string? payload = null, string? taskId = null, int timeoutMs = 5000, CancellationToken ct = default);

    /// <summary>
    /// 确认命令被设备接受
    /// </summary>
    bool ConfirmAccepted(string commandId);

    /// <summary>
    /// 确认命令开始执行
    /// </summary>
    bool ConfirmExecuting(string commandId);

    /// <summary>
    /// 确认命令执行完成
    /// </summary>
    bool ConfirmCompleted(string commandId, string? result = null);

    /// <summary>
    /// 确认命令失败
    /// </summary>
    bool ConfirmFailed(string commandId, string? error = null);

    /// <summary>
    /// 确认命令超时
    /// </summary>
    bool ConfirmTimeout(string commandId);

    /// <summary>
    /// 确认命令被拒绝
    /// </summary>
    bool ConfirmRejected(string commandId, string? reason = null);

    /// <summary>
    /// 取消命令
    /// </summary>
    Task<bool> CancelCommandAsync(string commandId, CancellationToken ct = default);

    /// <summary>
    /// 获取命令记录
    /// </summary>
    DeviceCommandRecord? GetCommand(string commandId);

    /// <summary>
    /// 查询设备最近的命令
    /// </summary>
    IEnumerable<DeviceCommandRecord> GetDeviceCommands(string deviceId, int maxCount = 50);

    /// <summary>
    /// 查询未完成的命令
    /// </summary>
    IEnumerable<DeviceCommandRecord> GetPendingCommands();

    /// <summary>
    /// 查询超时命令
    /// </summary>
    IEnumerable<DeviceCommandRecord> GetTimeoutCommands();

    /// <summary>
    /// 获取命令统计
    /// </summary>
    CommandCenterStats GetStats();

    /// <summary>
    /// 清空命令历史
    /// </summary>
    void Clear();
}

/// <summary>
/// 命令中心统计
/// </summary>
public class CommandCenterStats
{
    public int TotalCommands { get; set; }
    public int CompletedCommands { get; set; }
    public int FailedCommands { get; set; }
    public int TimeoutCommands { get; set; }
    public int PendingCommands { get; set; }
    public double AvgCompletionTimeMs { get; set; }
}
