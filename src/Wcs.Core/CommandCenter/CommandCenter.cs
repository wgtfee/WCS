using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Label;
using Wcs.Core.PlcSubsystem.Modbus;
using Wcs.Core.PlcSubsystem.OpcUa;
using Wcs.Core.PlcSubsystem.S7;

namespace Wcs.Core.CommandCenter;

public class CommandCenter : ICommandCenter, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceCommandRecord> _commands = new();
    private readonly PlcWriter _plcWriter;
    private readonly IEnumerable<ITagSerializer> _tagSerializers;
    private readonly ISqlSugarClient? _db;
    private readonly ILogger<CommandCenter>? _logger;
    private readonly Timer _timeoutTimer;
    private bool _disposed;

    /// <summary>特性类型 → 匹配的序列化器索引</summary>
    private static readonly Dictionary<Type, Type> AttrToSerializer = new()
    {
        [typeof(PlcStructAttribute)] = typeof(PlcTagSerializer),
        [typeof(PlcModbusBlockAttribute)] = typeof(ModbusTagSerializer),
        [typeof(PlcOpcUaBlockAttribute)] = typeof(OpcUaTagSerializer),
        [typeof(PlcBlockAttribute)] = typeof(Snap7TagSerializer),
    };

    public CommandCenter(PlcWriter plcWriter,
        IEnumerable<ITagSerializer> tagSerializers,
        ILogger<CommandCenter>? logger = null,
        ISqlSugarClient? db = null)
    {
        _plcWriter = plcWriter;
        _tagSerializers = tagSerializers;
        _db = db;
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

    /// <summary>发送 [PlcBlock] struct 命令（Snap7 专用）</summary>
    public async Task<DeviceCommandRecord> SendStructuredCommandAsync<T>(
        string deviceId, string commandType, T commandData,
        string? taskId = null, CancellationToken ct = default) where T : struct
    {
        var record = CreateRecord(commandType, deviceId, taskId);
        _commands[record.CommandId] = record;

        _logger?.LogInformation("[Cmd] {Type}(struct) → {Device}", commandType, deviceId);

        var success = await _plcWriter.WriteStructAsync(commandData, deviceId, taskId, commandType);
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

    /// <summary>
    /// 发送标签命令 — 自动根据命令类上的特性路由到对应的协议序列化器
    ///
    /// 支持的特性：
    ///   [PlcStruct]      → S7CommPlus
    ///   [PlcModbusBlock] → Modbus
    ///   [PlcOpcUaBlock]  → OPC UA
    ///   [PlcBlock]       → Snap7（通过 Snap7TagSerializer 适配）
    /// </summary>
    public async Task<DeviceCommandRecord> SendTagCommandAsync<T>(
        string deviceId, string commandType, T commandData,
        string? taskId = null, CancellationToken ct = default)
    {
        var record = CreateRecord(commandType, deviceId, taskId);
        _commands[record.CommandId] = record;

        var serializer = ResolveSerializer(commandData);
        if (serializer == null)
        {
            record.Status = DeviceCommandStatus.Failed;
            record.ErrorMessage = "找不到匹配的命令序列化器，请检查是否注册了对应协议";
            return record;
        }

        _logger?.LogInformation("[Cmd] {Type}({Protocol}) → {Device}",
            commandType, serializer.GetType().Name, deviceId);

        try
        {
            // 检查连接健康
            var healthy = await serializer.CheckHealthAsync();
            if (!healthy)
            {
                record.Status = DeviceCommandStatus.Failed;
                record.ErrorMessage = $"PLC 连接不可用 ({serializer.GetType().Name})";
                _logger?.LogWarning("[Cmd] ❌ {Type} 连接不可用", commandType);
                return record;
            }

            await serializer.WriteAsync(commandData);
            record.Status = DeviceCommandStatus.Sent;
            record.SentTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            record.Status = DeviceCommandStatus.Failed;
            record.ErrorMessage = ex.Message;
            _logger?.LogError(ex, "[Cmd] ❌ {Type} → {Device}", commandType, deviceId);
        }
        return record;
    }

    /// <summary>根据命令对象的特性查找匹配的序列化器</summary>
    private ITagSerializer? ResolveSerializer(object command)
    {
        var type = command.GetType();

        // 遍历特性 → 序列化器映射表，找第一个匹配的
        foreach (var (attrType, serializerType) in AttrToSerializer)
        {
            if (type.GetCustomAttribute(attrType) != null)
                return _tagSerializers.FirstOrDefault(s => s.GetType() == serializerType);
        }
        return null;
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
