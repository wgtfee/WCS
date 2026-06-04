using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Pools;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.SignalSnapshot;

namespace Wcs.Application;

public static class PlcRegistrationExtension
{
    /// <summary>
    /// 注册 PLC 子系统核心组件（两种模式都必需）
    /// connectToPlc=true  时额外创建真实 PLC 连接池
    /// connectToPlc=false 时只注册 DB 映射、EventDetector、PlcWriter，不连接硬件
    /// </summary>
    public static IServiceCollection AddWcsPlc(this IServiceCollection services, IConfiguration config, bool connectToPlc = false)
    {
        var registry = new PlcStructRegistry();

        if (connectToPlc)
        {
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
        }

        var blocks = config.GetSection("PlcBlocks").Get<List<PlcBlockConfig>>()
            ?? new List<PlcBlockConfig>();
        if (blocks.Count > 0)
        {
            registry.RegisterFromConfig(blocks);
            Console.WriteLine($"[WcsPlc] {blocks.Count} DB块");
        }

        services.AddSingleton(registry);
        services.AddSingleton<SignalSnapshotCenter>();
        services.AddSingleton<EventDetector>();
        services.AddSingleton<PlcWriter>();

        if (connectToPlc)
            services.AddSingleton<S7PollingService>();

        return services;
    }
}
