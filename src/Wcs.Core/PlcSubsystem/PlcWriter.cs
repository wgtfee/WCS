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
        _logger?.LogInformation("[PlcWriter] DB 注入: {Status}", db != null ? "✅ 已连接" : "⚠️ 未连接");
    }

    public async Task<bool> WriteCommandAsync(string plcName, int dbBlock, int startByte, object command,
        string? deviceId = null, string? taskId = null, string? commandType = null)
    {
        var conn = _registry.WritePool.Get(plcName);
        if (conn == null)
        {
            _logger?.LogInformation("[Write] {Plc} DB{Block}: 无写连接(模拟模式)，跳过实际写入", plcName, dbBlock);
            return false;
        }

        try
        {
            var size = PlcSerializer.CalculateBufferSize(command.GetType());
            var data = PlcSerializer.Serialize(command, startByte + size);

            var hexLen = Math.Max(0, Math.Min(191, data.Length * 3 - 1));
            var dataHex = data.Length > 0 ? BitConverter.ToString(data).Replace("-", " ")[..hexLen] : "";

            _logger?.LogInformation("[Write] 📝 数据: {Plc} DB{Block}@{Start} = [{Data}] ({Size}B)",
                plcName, dbBlock, startByte, dataHex, data.Length);

            var (result, error) = await conn.WriteAsync(dbBlock, startByte, data);

            await LogWriteAsync(plcName, dbBlock, startByte, commandType ?? "Write", deviceId, taskId, dataHex, data.Length, result == 0, result == 0 ? null : error);

            if (result == 0)
            {
                _logger?.LogInformation("[Write] ✅ {Plc} DB{Block}@{Start} ({Size}B)", plcName, dbBlock, startByte, data.Length);
                return true;
            }

            _logger?.LogWarning("[Write] ❌ {Plc} DB{Block}: {Error}", plcName, dbBlock, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Write] {Plc} DB{Block}", plcName, dbBlock);
            await LogWriteAsync(plcName, dbBlock, startByte, commandType ?? "Write", deviceId, taskId, null, 0, false, ex.Message);
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

    private async Task LogWriteAsync(string plcName, int dbBlock, int startByte, string commandType,
        string? deviceId, string? taskId, string? dataHex, int dataLength, bool success, string? error)
    {
        if (_db == null)
        {
            _logger?.LogInformation("[WriteLog] DB 未连接，跳过日志写入");
            return;
        }
        try
        {
            await _db.Insertable(new PlcWriteLogEntity
            {
                Id = DateTime.UtcNow.Ticks + Random.Shared.Next(0, 9999),
                PlcName = plcName,
                DbBlock = dbBlock,
                StartByte = startByte,
                CommandType = commandType,
                DeviceId = deviceId,
                TaskId = taskId,
                DataHex = dataHex,
                DataLength = dataLength,
                Success = success,
                ErrorMessage = error,
                WriteTime = DateTime.UtcNow
            }).ExecuteCommandAsync();
            if (dataHex != null)
                _logger?.LogInformation("[WriteLog] ✅ 已写入 Wcs_PlcWriteLog: {Plc} DB{Block} = [{Hex}]",
                    plcName, dbBlock, dataHex);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WriteLog] 写入失败");
        }
    }

    private static PlcBlockAttribute? GetPlcBlockAttr<T>() where T : struct
        => typeof(T).GetCustomAttribute<PlcBlockAttribute>();
}
