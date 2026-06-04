using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Pools;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Application;

public static class PlcRegistrationExtension
{
    public static IServiceCollection AddWcsPlc(this IServiceCollection services, IConfiguration config)
    {
        var registry = new PlcStructRegistry();

        var connections = config.GetSection("PlcConnections").Get<List<PlcConnectionConfig>>()
            ?? new List<PlcConnectionConfig>();
        foreach (var conn in connections)
        {
            if (string.IsNullOrWhiteSpace(conn.PlcName) || string.IsNullOrWhiteSpace(conn.Address))
                continue;
            registry.AddReadConnection(conn.PlcName, conn.Address, conn.Rack, conn.Slot);
            registry.AddWriteConnection(conn.PlcName, conn.Address, conn.Rack, conn.Slot);
            Console.WriteLine($"[WcsPlc] 🏭 {conn.PlcName} @ {conn.Address} (读+写双池)");
        }

        var blocks = config.GetSection("PlcBlocks").Get<List<PlcBlockConfig>>()
            ?? new List<PlcBlockConfig>();
        if (blocks.Count > 0)
        {
            registry.RegisterFromConfig(blocks);
            Console.WriteLine($"[WcsPlc] 📦 {blocks.Count} 个 DB 块已注册");
        }

        services.AddSingleton(registry);

        // EventDetector — PLC 状态变化 → 业务事件
        services.AddSingleton(sp =>
            new EventDetector(
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<ILogger<EventDetector>>()));

        // 轮询服务
        services.AddSingleton(sp =>
            new S7PollingService(
                sp.GetRequiredService<PlcStructRegistry>(),
                sp.GetRequiredService<IStateCenter>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<EventDetector>(),
                sp.GetRequiredService<ILogger<S7PollingService>>()));

        return services;
    }
}
