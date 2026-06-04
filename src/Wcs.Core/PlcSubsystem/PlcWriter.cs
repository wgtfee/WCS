namespace Wcs.Core.PlcSubsystem;

using System.Reflection;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Pools;
using Wcs.Core.PlcSubsystem.S7;

/// <summary>
/// PLC 写入器 — 整个系统只有此处写 PLC
///
/// 整个系统只有 CommandCenter 调用 PlcWriter
/// 整个系统只有 PlcWriter 调用 WritePool
///
/// 写命令的两种方式：
///   1. WriteCommandAsync(plc, db, start, command) —— 显式指定位置
///   2. WriteStructAsync(command) —— 从 [PlcBlock] 特性自动发现位置
/// </summary>
public class PlcWriter
{
    private readonly PlcStructRegistry _registry;
    private readonly ILogger<PlcWriter>? _logger;

    public PlcWriter(PlcStructRegistry registry, ILogger<PlcWriter>? logger = null)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>显式指定位置的写入</summary>
    public async Task<bool> WriteCommandAsync(string plcName, int dbBlock, int startByte, object command)
    {
        var conn = _registry.WritePool.Get(plcName);
        if (conn == null)
        {
            _logger?.LogWarning("[Write] {Plc} 无写连接", plcName);
            return false;
        }

        try
        {
            var size = PlcSerializer.CalculateBufferSize(command.GetType());
            var data = PlcSerializer.Serialize(command, startByte + size);
            var (result, error) = await conn.WriteAsync(dbBlock, startByte, data);

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
            return false;
        }
    }

    /// <summary>
    /// 从 [PlcBlock] 特性自动发现目标 PLC/DB 块，写入命令
    ///
    /// 使用示例：
    ///   var cmd = new ConveyorCommand { Start = true, Speed = 1500 };
    ///   await writer.WriteStructAsync(cmd);
    ///   // 自动从 [PlcBlock("PLC1", 101)] 获知目标，无需手动指定
    /// </summary>
    public async Task<bool> WriteStructAsync<T>(T command) where T : struct
    {
        var blockAttr = GetPlcBlockAttr<T>();
        if (blockAttr == null)
        {
            _logger?.LogWarning("[Write] {Type} 缺少 [PlcBlock] 特性", typeof(T).Name);
            return false;
        }

        return await WriteCommandAsync(blockAttr.PlcName, blockAttr.DbBlock, blockAttr.StartByte, command);
    }

    /// <summary>获取 struct 上的 [PlcBlock] 特性</summary>
    private static PlcBlockAttribute? GetPlcBlockAttr<T>() where T : struct
    {
        return typeof(T).GetCustomAttribute<PlcBlockAttribute>();
    }
}
