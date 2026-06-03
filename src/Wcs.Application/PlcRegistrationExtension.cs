using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Application;

/// <summary>
/// PLC 注册扩展 — 从 appsettings.json 读取配置，自动创建连接池和轮询服务
///
/// 使用方式（Program.cs）：
///   builder.Services.AddWcsPlc(builder.Configuration);
///   然后后台服务自动启动轮询。
///
/// 配置项（appsettings.json）：
///   "PlcConnections": [ { "PlcName":"PLC1","Address":"192.168.0.1","Rack":0,"Slot":0 } ]
///   "PlcBlocks": [
///     { "PlcName":"PLC1","BlockNumber":1,"Length":200,"PollIntervalMs":500,
///       "StructType":"MyApp.DB1_Struct, MyApp" }
///   ]
/// </summary>
public static class PlcRegistrationExtension
{
    /// <summary>
    /// 从配置注册 PLC 连接和 DB 块映射
    /// </summary>
    public static IServiceCollection AddWcsPlc(this IServiceCollection services, IConfiguration config)
    {
        // 1. 创建注册表
        var registry = new PlcStructRegistry();

        // 2. 读取 PLC 连接配置 → 创建连接池
        var connections = config.GetSection("PlcConnections").Get<List<PlcConnectionConfig>>()
            ?? new List<PlcConnectionConfig>();

        foreach (var conn in connections)
        {
            if (string.IsNullOrWhiteSpace(conn.PlcName) || string.IsNullOrWhiteSpace(conn.Address))
                continue;

            registry.GetOrCreatePool(conn.PlcName, conn.Address, conn.Rack, conn.Slot);
            Log($"🏭 PLC 连接已注册: {conn.PlcName} @ {conn.Address}");
        }

        // 3. 读取 DB 块配置 → 注册 struct 映射
        var blocks = config.GetSection("PlcBlocks").Get<List<PlcBlockConfig>>()
            ?? new List<PlcBlockConfig>();

        if (blocks.Count > 0)
        {
            registry.RegisterFromConfig(blocks);
            Log($"📦 {blocks.Count} 个 DB 块已注册");
        }
        else
        {
            Log("⚠️ PlcBlocks 配置为空，未注册任何 DB 块");
        }

        // 4. 注册到 DI
        services.AddSingleton(registry);

        // 5. 注册 StructBridge（验证管道）
        services.AddSingleton(sp =>
        {
            var bridge = new StructBridge(
                sp.GetRequiredService<IStateCenter>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<ILogger<StructBridge>>());

            // 扩展点：在此处注册自定义验证器
            // bridge.RegisterValidator(new MyStationValidator(sp.GetRequiredService<...>()));

            return bridge;
        });

        // 6. 注册轮询服务
        services.AddSingleton(sp =>
        {
            var reg = sp.GetRequiredService<PlcStructRegistry>();
            var bridge = sp.GetRequiredService<StructBridge>();
            var logger = sp.GetRequiredService<ILogger<S7PollingService>>();
            return new S7PollingService(reg, bridge, logger);
        });

        return services;
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"[WcsPlc] {msg}");
    }
}
