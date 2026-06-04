namespace Wcs.Core.PlcSubsystem;

using System.Reflection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.PlcSubsystem.Pools;
using Wcs.Core.PlcSubsystem.S7;

public class PlcWriter
{
    private readonly PlcStructRegistry _registry;
    private readonly ISqlSugarClient? _db;
    private readonly ILogger<PlcWriter>? _logger;

    public PlcWriter(PlcStructRegistry registry, ISqlSugarClient? db = null, ILogger<PlcWriter>? logger = null)
    {
        _registry = registry;
        _db = db;
        _logger = logger;
    }

    public async Task<bool> WriteCommandAsync(string plcName, int dbBlock, int startByte, object command,
        string? deviceId = null, string? taskId = null, string? commandType = null)
    {
        var conn = _registry.WritePool.Get(plcName);
        if (conn == null)
        {
            _logger?.LogInformation("[Write] {Plc} DB{Block}: 无写连接(模拟模式)", plcName, dbBlock);
            await WriteLogsAsync(plcName, dbBlock, startByte, commandType ?? "Write", deviceId, taskId, null, 0, conn == null, conn == null ? "模拟模式" : null);
            return false;
        }

        try
        {
            var size = PlcSerializer.CalculateBufferSize(command.GetType());
            var data = PlcSerializer.Serialize(command, startByte + size);
            var hexLen = Math.Max(0, Math.Min(191, data.Length * 3 - 1));
            var dataHex = data.Length > 0 ? BitConverter.ToString(data).Replace("-", " ")[..hexLen] : "";

            var (result, error) = await conn.WriteAsync(dbBlock, startByte, data);
            await WriteLogsAsync(plcName, dbBlock, startByte, commandType ?? "Write", deviceId, taskId, dataHex, data.Length, result == 0, result == 0 ? null : error);

            if (result == 0) { _logger?.LogInformation("[Write] ✅ {Plc} DB{Block} ({Size}B)", plcName, dbBlock, data.Length); return true; }
            _logger?.LogWarning("[Write] ❌ {Plc} DB{Block}: {Error}", plcName, dbBlock, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Write] {Plc} DB{Block}", plcName, dbBlock);
            await WriteLogsAsync(plcName, dbBlock, startByte, commandType ?? "Write", deviceId, taskId, null, 0, false, ex.Message);
            return false;
        }
    }

    public async Task<bool> WriteStructAsync<T>(T command, string? deviceId = null,
        string? taskId = null, string? commandType = null) where T : struct
    {
        var blockAttr = GetPlcBlockAttr<T>();
        if (blockAttr == null)
        {
            _logger?.LogWarning("[Write] {Type} 缺少 [PlcBlock] 特性", typeof(T).Name);
            return false;
        }
        return await WriteCommandAsync(blockAttr.PlcName, blockAttr.DbBlock, blockAttr.StartByte,
            command, deviceId, taskId, commandType ?? typeof(T).Name);
    }

    /// <summary>写入 Wcs_PlcWriteLog + Wcs_CommandLog</summary>
    private async Task WriteLogsAsync(string plcName, int dbBlock, int startByte, string commandType,
        string? deviceId, string? taskId, string? dataHex, int dataLength, bool success, string? error)
    {
        if (_db == null) return;
        try
        {
            var now = DateTime.UtcNow;
            var id = now.Ticks + Random.Shared.Next(0, 9999);

            // Wcs_PlcWriteLog
            await _db.Insertable(new PlcWriteLogEntity
            {
                Id = id, PlcName = plcName, DbBlock = dbBlock, StartByte = startByte,
                CommandType = commandType, DeviceId = deviceId, TaskId = taskId,
                DataHex = dataHex, DataLength = dataLength, Success = success,
                ErrorMessage = error, WriteTime = now
            }).ExecuteCommandAsync();

            // Wcs_CommandLog
            await _db.Insertable(new CommandLogEntity
            {
                CommandId = Guid.NewGuid().ToString("N"),
                CommandType = commandType,
                DeviceId = deviceId ?? "",
                TaskId = taskId,
                Status = success ? 5 : 6,
                Payload = dataHex,
                CreatedTime = now,
                SentTime = now,
                CompletedTime = success ? now : null,
                TimeoutMs = 5000,
                ErrorMessage = error
            }).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WriteLog] 写入失败");
        }
    }

    private static PlcBlockAttribute? GetPlcBlockAttr<T>() where T : struct
        => typeof(T).GetCustomAttribute<PlcBlockAttribute>();
}
