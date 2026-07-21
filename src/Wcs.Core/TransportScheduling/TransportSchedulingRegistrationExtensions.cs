namespace Wcs.Core.TransportScheduling;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;

public static class TransportSchedulingRegistrationExtensions
{
    /// <summary>
    /// 注册 EMS/RGV 统一调度、执行、持久化恢复与设备驱动组件。
    /// 生产环境应在 Infrastructure 中覆盖 ITransportStateStore 和 ITransportVehicleDriver。
    /// </summary>
    public static IServiceCollection AddUnifiedTransportScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TopologyGraph>();
        services.TryAddSingleton<ITransportRouteCenter, TransportRouteCenter>();
        services.TryAddSingleton<ITransportVehicleRegistry, InMemoryTransportVehicleRegistry>();
        services.TryAddSingleton<ITransportVehicleSelector, DefaultTransportVehicleSelector>();
        services.TryAddSingleton<IRouteReservationManager, InMemoryRouteReservationManager>();
        services.TryAddSingleton<IUnifiedTransportDispatchEngine, UnifiedTransportDispatchEngine>();
        services.TryAddSingleton<ITransportExecutionEngine, InMemoryTransportExecutionEngine>();

        services.TryAddSingleton<ITransportStateStore, InMemoryTransportStateStore>();
        services.AddSingleton<ITransportVehicleDriver>(_ => new SimulatorTransportVehicleDriver(TransportVehicleKind.Ems));
        services.AddSingleton<ITransportVehicleDriver>(_ => new SimulatorTransportVehicleDriver(TransportVehicleKind.Rgv));
        services.TryAddSingleton<ITransportDriverResolver, TransportDriverResolver>();
        services.TryAddSingleton<ITransportCommandDispatcher, TransportCommandDispatcher>();
        services.TryAddSingleton<ITransportRecoveryCoordinator, TransportRecoveryCoordinator>();

        return services;
    }
}
