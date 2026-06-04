namespace Wcs.Core.CommandCenter;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem;

public class CommandCenter : ICommandCenter, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceCommandRecord> _commands = new();
    private readonly PlcWriter _plcWriter;
    private readonly ILogger<CommandCenter>? _logger;
    private readonly Timer _timeoutTimer;
    private bool _disposed;

    public CommandCenter(PlcWriter plcWriter, ILogger<CommandCenter>? logger = null)
    {
        _plcWriter = plcWriter;
        _logger = logger;
        _timeoutTimer = new Timer(CheckTimeouts, null, 2000, 2000);
    }

    public async Task<DeviceCommandRecord> SendCommandAsync(
        string deviceId, string commandType,
        string? payload = null, string? taskId = null,
        int timeoutMs = 5000, CancellationToken ct = default)
    {
        var record = CreateRecord(commandType, deviceId, taskId, timeoutMs);
        record.Source = "TaskEngine";
        _commands[record.CommandId] = record;
        record.Status = DeviceCommandStatus.Sent;
        record.SentTime = DateTime.UtcNow;
        _logger?.LogInformation("[Cmd] {Type} → {Device}", commandType, deviceId);
        return record;
    }

    /// <summary>
    /// 发送带 [PlcBlock] + [PlcOffset] 的结构化命令
    /// 自动从 [PlcBlock] 特性中读取目标 PLC/DB 块，无需外部映射
    ///
    /// 用法：
    ///   var cmd = new ConveyorCommand { Start = true };
    ///   await cmdCenter.SendStructuredCommandAsync("CV01", "StartConveyor", cmd);
    ///   // 自动通过 [PlcBlock("PLC1", 101)] 写入 PLC1.DB101
    /// </summary>
    public async Task<DeviceCommandRecord> SendStructuredCommandAsync<T>(
        string deviceId, string commandType, T commandData,
        string? taskId = null, CancellationToken ct = default) where T : struct
    {
        var record = CreateRecord(commandType, deviceId, taskId);
        _commands[record.CommandId] = record;

        _logger?.LogInformation("[Cmd] {Type}(struct) → {Device}", commandType, deviceId);

        var success = await _plcWriter.WriteStructAsync(commandData);
        if (!success)
        {
            record.Status = DeviceCommandStatus.Failed;
            record.ErrorMessage = "PLC 写入失败";
            return record;
        }

        record.Status = DeviceCommandStatus.Sent;
        record.SentTime = DateTime.UtcNow;
        return record;
    }

    private DeviceCommandRecord CreateRecord(string commandType, string deviceId,
        string? taskId = null, int timeoutMs = 5000) => new()
    {
        CommandType = commandType, DeviceId = deviceId, TaskId = taskId,
        TimeoutMs = timeoutMs, Status = DeviceCommandStatus.Created,
        CreatedTime = DateTime.UtcNow
    };

    public bool ConfirmAcked(string commandId) => UpdateStatus(commandId, DeviceCommandStatus.Acked);
    public bool ConfirmExecuting(string commandId) => UpdateStatus(commandId, DeviceCommandStatus.Executing);
    public bool ConfirmDone(string commandId) => UpdateStatus(commandId, DeviceCommandStatus.Done);

    public bool ConfirmCompleted(string commandId, string? result = null)
    {
        if (!_commands.TryGetValue(commandId, out var r)) return false;
        r.Status = DeviceCommandStatus.Completed; r.CompletedTime = DateTime.UtcNow;
        if (result != null) r.Payload = result; return true;
    }

    public bool ConfirmFailed(string commandId, string? error = null)
    {
        if (!_commands.TryGetValue(commandId, out var r)) return false;
        r.Status = DeviceCommandStatus.Failed; r.ErrorMessage = error; r.CompletedTime = DateTime.UtcNow; return true;
    }

    public bool ConfirmTimeout(string commandId) => UpdateStatus(commandId, DeviceCommandStatus.Timeout);

    public bool ConfirmRejected(string commandId, string? reason = null)
    {
        if (!_commands.TryGetValue(commandId, out var r)) return false;
        r.Status = DeviceCommandStatus.Rejected; r.ErrorMessage = reason; r.CompletedTime = DateTime.UtcNow; return true;
    }

    public async Task<bool> CancelCommandAsync(string commandId, CancellationToken ct = default)
    { await Task.CompletedTask; return UpdateStatus(commandId, DeviceCommandStatus.Cancelled); }

    public DeviceCommandRecord? GetCommand(string commandId)
    { _commands.TryGetValue(commandId, out var r); return r; }

    public IEnumerable<DeviceCommandRecord> GetDeviceCommands(string deviceId, int max = 50)
        => _commands.Values.Where(c => c.DeviceId == deviceId).OrderByDescending(c => c.CreatedTime).Take(max);

    public IEnumerable<DeviceCommandRecord> GetPendingCommands()
        => _commands.Values.Where(c => c.Status is DeviceCommandStatus.Sent or DeviceCommandStatus.Acked or DeviceCommandStatus.Executing);

    public IEnumerable<DeviceCommandRecord> GetTimeoutCommands()
        => _commands.Values.Where(c => c.Status == DeviceCommandStatus.Timeout);

    public CommandCenterStats GetStats()
    {
        var comp = _commands.Values.Where(c => c.Status == DeviceCommandStatus.Completed && c.CompletedTime.HasValue);
        return new CommandCenterStats
        {
            TotalCommands = _commands.Count,
            CompletedCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Completed),
            FailedCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Failed),
            TimeoutCommands = _commands.Values.Count(c => c.Status == DeviceCommandStatus.Timeout),
            PendingCommands = GetPendingCommands().Count(),
            AvgCompletionTimeMs = comp.Any() ? comp.Average(c => (c.CompletedTime!.Value - c.CreatedTime).TotalMilliseconds) : 0
        };
    }

    public void Clear() => _commands.Clear();
    public void Dispose() { if (!_disposed) { _disposed = true; _timeoutTimer.Dispose(); } }

    private bool UpdateStatus(string commandId, DeviceCommandStatus s)
    {
        if (!_commands.TryGetValue(commandId, out var r)) return false;
        r.Status = s;
        if (s is DeviceCommandStatus.Completed or DeviceCommandStatus.Failed
            or DeviceCommandStatus.Timeout or DeviceCommandStatus.Rejected)
            r.CompletedTime = DateTime.UtcNow;
        return true;
    }

    private void CheckTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var r in _commands.Values)
        {
            if (r.Status is DeviceCommandStatus.Completed or DeviceCommandStatus.Failed
                or DeviceCommandStatus.Timeout or DeviceCommandStatus.Rejected or DeviceCommandStatus.Cancelled) continue;
            if (!r.SentTime.HasValue) continue;
            if ((now - r.SentTime.Value).TotalMilliseconds > r.TimeoutMs)
            { r.Status = DeviceCommandStatus.Timeout; r.ErrorMessage = $"超时 {r.TimeoutMs}ms"; r.CompletedTime = now; }
        }
    }
}
