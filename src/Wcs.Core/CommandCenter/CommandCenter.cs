namespace Wcs.Core.CommandCenter;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem;

/// <summary>
/// 命令中心实现 — 统一管理所有设备命令的完整生命周期
///
/// ActionNode → CommandCenter.SendCommandAsync() → CommandQueue → PLC
///
/// 功能：
/// - 命令状态机（Created→Sent→Accepted→Executing→Completed/Failed/Timeout/Rejected）
/// - 超时检测
/// - 重试机制
/// - 审计追踪
/// - 统计报告
/// </summary>
public class CommandCenter : ICommandCenter, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceCommandRecord> _commands = new();
    private readonly ILogger<CommandCenter>? _logger;
    private readonly Timer _timeoutTimer;
    private readonly TimeSpan _timeoutCheckInterval = TimeSpan.FromSeconds(2);
    private bool _disposed;

    public CommandCenter(ILogger<CommandCenter>? logger = null)
    {
        _logger = logger;
        _timeoutTimer = new Timer(CheckTimeouts, null,
            _timeoutCheckInterval, _timeoutCheckInterval);
    }

    /// <summary>
    /// 发送命令 — 创建命令记录并标记为 Sent
    /// 实际 PLC 写入由调用方完成（ActionNode 的 Handler）
    /// </summary>
    public async Task<DeviceCommandRecord> SendCommandAsync(
        string deviceId, string commandType,
        string? payload = null, string? taskId = null,
        int timeoutMs = 5000, CancellationToken ct = default)
    {
        var record = new DeviceCommandRecord
        {
            CommandType = commandType,
            DeviceId = deviceId,
            Payload = payload,
            TaskId = taskId,
            TimeoutMs = timeoutMs,
            Status = DeviceCommandStatus.Created,
            CreatedTime = DateTime.UtcNow,
            Source = "TaskEngine"
        };

        _commands[record.CommandId] = record;

        // 标记为已发送
        record.Status = DeviceCommandStatus.Sent;
        record.SentTime = DateTime.UtcNow;

        _logger?.LogInformation(
            "Command {CommandId}: {CommandType} -> {DeviceId} (Task={TaskId})",
            record.CommandId, commandType, deviceId, taskId);

        await Task.CompletedTask;
        return record;
    }

    public bool ConfirmAccepted(string commandId)
    {
        return UpdateStatus(commandId, DeviceCommandStatus.Accepted);
    }

    public bool ConfirmExecuting(string commandId)
    {
        return UpdateStatus(commandId, DeviceCommandStatus.Executing);
    }

    public bool ConfirmCompleted(string commandId, string? result = null)
    {
        if (!_commands.TryGetValue(commandId, out var record))
            return false;

        record.Status = DeviceCommandStatus.Completed;
        record.CompletedTime = DateTime.UtcNow;
        if (result != null) record.Payload = result;

        _logger?.LogInformation("Command {CommandId}: {CommandType} completed on {DeviceId}",
            commandId, record.CommandType, record.DeviceId);
        return true;
    }

    public bool ConfirmFailed(string commandId, string? error = null)
    {
        if (!_commands.TryGetValue(commandId, out var record))
            return false;

        record.Status = DeviceCommandStatus.Failed;
        record.ErrorMessage = error;
        record.CompletedTime = DateTime.UtcNow;

        _logger?.LogWarning("Command {CommandId}: {CommandType} failed on {DeviceId}: {Error}",
            commandId, record.CommandType, record.DeviceId, error);
        return true;
    }

    public bool ConfirmTimeout(string commandId)
    {
        return UpdateStatus(commandId, DeviceCommandStatus.Timeout);
    }

    public bool ConfirmRejected(string commandId, string? reason = null)
    {
        if (!_commands.TryGetValue(commandId, out var record))
            return false;

        record.Status = DeviceCommandStatus.Rejected;
        record.ErrorMessage = reason;
        record.CompletedTime = DateTime.UtcNow;

        _logger?.LogWarning("Command {CommandId}: {CommandType} rejected by {DeviceId}: {Reason}",
            commandId, record.CommandType, record.DeviceId, reason);
        return true;
    }

    public async Task<bool> CancelCommandAsync(string commandId, CancellationToken ct = default)
    {
        var result = UpdateStatus(commandId, DeviceCommandStatus.Cancelled);
        await Task.CompletedTask;
        return result;
    }

    public DeviceCommandRecord? GetCommand(string commandId)
    {
        _commands.TryGetValue(commandId, out var record);
        return record;
    }

    public IEnumerable<DeviceCommandRecord> GetDeviceCommands(string deviceId, int maxCount = 50)
    {
        return _commands.Values
            .Where(c => c.DeviceId == deviceId)
            .OrderByDescending(c => c.CreatedTime)
            .Take(maxCount);
    }

    public IEnumerable<DeviceCommandRecord> GetPendingCommands()
    {
        return _commands.Values
            .Where(c => c.Status == DeviceCommandStatus.Sent
                     || c.Status == DeviceCommandStatus.Accepted
                     || c.Status == DeviceCommandStatus.Executing);
    }

    public IEnumerable<DeviceCommandRecord> GetTimeoutCommands()
    {
        return _commands.Values
            .Where(c => c.Status == DeviceCommandStatus.Timeout);
    }

    public CommandCenterStats GetStats()
    {
        var completed = _commands.Values
            .Where(c => c.Status == DeviceCommandStatus.Completed && c.CompletedTime.HasValue);

        var avgMs = completed.Any()
            ? completed.Average(c => (c.CompletedTime!.Value - c.CreatedTime).TotalMilliseconds)
            : 0;

        return new CommandCenterStats
        {
            TotalCommands = _commands.Count,
            CompletedCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Completed),
            FailedCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Failed),
            TimeoutCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Timeout),
            PendingCommands = GetPendingCommands().Count(),
            AvgCompletionTimeMs = avgMs
        };
    }

    public void Clear()
    {
        _commands.Clear();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timeoutTimer.Dispose();
        }
    }

    private bool UpdateStatus(string commandId, DeviceCommandStatus newStatus)
    {
        if (!_commands.TryGetValue(commandId, out var record))
            return false;

        record.Status = newStatus;
        if (newStatus is DeviceCommandStatus.Completed or DeviceCommandStatus.Failed
            or DeviceCommandStatus.Timeout or DeviceCommandStatus.Rejected)
        {
            record.CompletedTime = DateTime.UtcNow;
        }
        return true;
    }

    private void CheckTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var record in _commands.Values)
        {
            // 只检查已发送但未完成的命令
            if (record.Status is DeviceCommandStatus.Completed or DeviceCommandStatus.Failed
                or DeviceCommandStatus.Timeout or DeviceCommandStatus.Rejected
                or DeviceCommandStatus.Cancelled)
                continue;

            if (!record.SentTime.HasValue) continue;

            var elapsed = (now - record.SentTime.Value).TotalMilliseconds;
            if (elapsed > record.TimeoutMs)
            {
                record.Status = DeviceCommandStatus.Timeout;
                record.ErrorMessage = $"Command timed out after {record.TimeoutMs}ms";
                record.CompletedTime = now;

                _logger?.LogWarning(
                    "Command {CommandId}: {CommandType} timeout after {Elapsed}ms (limit {TimeoutMs}ms)",
                    record.CommandId, record.CommandType, elapsed, record.TimeoutMs);
            }
        }
    }
}
