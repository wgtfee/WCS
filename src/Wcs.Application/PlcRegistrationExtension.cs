using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Pools;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.SignalSnapshot;
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
            Console.WriteLine($"[WcsPlc] {conn.PlcName} @ {conn.Address} (RW双池)");
        }

        var blocks = config.GetSection("PlcBlocks").Get<List<PlcBlockConfig>>()
            ?? new List<PlcBlockConfig>();
        if (blocks.Count > 0)
        {
            registry.RegisterFromConfig(blocks);
            Console.WriteLine($"[WcsPlc] {blocks.Count} DB块");
        }

        services.AddSingleton(registry);

        // 信号快照中心（Current/Previous 统一管理）
        services.AddSingleton<SignalSnapshotCenter>();

        // EventDetector（边沿检测 → 业务事件）
        services.AddSingleton<EventDetector>();

        // 轮询服务
        services.AddSingleton<S7PollingService>();

        return services;
    }
}
