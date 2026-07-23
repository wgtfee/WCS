using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签写入器 — 将标签修饰的对象写回 PLC
///
/// 对标 PlcWriter，差异：
///   - PlcWriter：Snap7 专用，按 [PlcBlock]+[PlcOffset] 序列化为 byte[] → WritePool
///   - TagWriter：协议无关，通过 ITagSerializer 写入
///
/// 使用示例：
///   // S7CommPlus
///   var writer = new TagWriter(new PlcTagSerializer(s7plusClient), db, logger);
///   await writer.WriteAsync(cmd, deviceId: "CV01");
///
///   // Modbus
///   var writer = new TagWriter(new ModbusTagSerializer(modbusClient), db, logger);
///   await writer.WriteAsync(cmd);
///
///   // OPC UA
///   var writer = new TagWriter(new OpcUaTagSerializer(opcuaClient), db, logger);
///   await writer.WriteAsync(cmd);
/// </summary>
public class TagWriter
{
    private readonly ITagSerializer _serializer;
    private readonly ISqlSugarClient? _db;
    private readonly ILogger<TagWriter>? _logger;

    public TagWriter(
        ITagSerializer serializer,
        ISqlSugarClient? db = null,
        ILogger<TagWriter>? logger = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _db = db;
        _logger = logger;
    }

    /// <summary>写入对象到 PLC</summary>
    public async Task<bool> WriteAsync<T>(T command,
        string? deviceId = null,
        string? taskId = null,
        string? commandType = null) where T : class
    {
        commandType ??= typeof(T).Name;

        try
        {
            await _serializer.WriteAsync(command);

            await WriteLogsAsync(commandType, deviceId, taskId, true, null);
            _logger?.LogInformation("[TagWrite] ✅ {Type}", commandType);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TagWrite] ❌ {Type}", commandType);
            await WriteLogsAsync(commandType, deviceId, taskId, false, ex.Message);
            return false;
        }
    }

    private async Task WriteLogsAsync(
        string commandType, string? deviceId, string? taskId,
        bool success, string? error)
    {
        if (_db == null) return;
        try
        {
            var now = DateTime.UtcNow;
            var db = _db.CopyNew();

            await db.Insertable(new PlcWriteLogEntity
            {
                Id = SnowFlakeSingle.Instance.NextId(),
                PlcName = "", DbBlock = 0, StartByte = 0,
                CommandType = commandType, DeviceId = deviceId, TaskId = taskId,
                DataHex = "", DataLength = 0, Success = success,
                ErrorMessage = error, WriteTime = now
            }).ExecuteCommandAsync();

            await db.Insertable(new CommandLogEntity
            {
                CommandId = Guid.NewGuid().ToString("N"),
                CommandType = commandType, DeviceId = deviceId ?? "", TaskId = taskId,
                Status = success ? 5 : 6, Payload = "",
                CreatedTime = now, SentTime = now,
                CompletedTime = success ? now : null,
                TimeoutMs = 5000, ErrorMessage = error
            }).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TagWrite] 日志写入失败");
        }
    }
}
