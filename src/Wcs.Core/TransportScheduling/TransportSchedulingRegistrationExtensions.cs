namespace Wcs.Core.TransportScheduling;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;

public static class TransportSchedulingRegistrationExtensions
{
    /// <summary>
    /// 注册 EMS/RGV 统一调度、执行、持久化恢复、交通控制、充电调度、
    /// 故障换车、效率统计、配置治理、审计与设备驱动组件。
    /// 生产环境应在 Infrastructure 中覆盖内存存储，并按现场协议替换模拟车辆驱动。
    /// </summary>
    public static IServiceCollection AddUnifiedTransportScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TopologyGraph>();
        services.TryAddSingleton<ITransportRouteCenter, TransportRouteCenter>();
        services.TryAddSingleton<ITransportVehicleRegistry, InMemoryTransportVehicleRegistry>();
        services.TryAddSingleton<ITransportVehicleSelector, DefaultTransportVehicleSelector>();

        services.TryAddSingleton<TransportTrafficCoordinator>();
        services.TryAddSingleton<ITransportTrafficCoordinator>(sp => sp.GetRequiredService<TransportTrafficCoordinator>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<TransportTrafficCoordinator>());

        services.TryAddSingleton<InMemoryRouteReservationManager>();
        services.TryAddSingleton<IRouteReservationManager, TrafficAwareRouteReservationManager>();
        services.TryAddSingleton<IUnifiedTransportDispatchEngine, UnifiedTransportDispatchEngine>();
        services.TryAddSingleton<InMemoryTransportExecutionEngine>();
        services.TryAddSingleton<CoordinatedTransportExecutionEngine>();
        services.TryAddSingleton<ITransportExecutionEngine>(sp =>
            sp.GetRequiredService<CoordinatedTransportExecutionEngine>());
        services.TryAddSingleton<ITransportReassignmentExecutionControl>(sp =>
            sp.GetRequiredService<CoordinatedTransportExecutionEngine>());
        services.TryAddSingleton<ITransportDeadlockService, TransportDeadlockService>();

        services.TryAddSingleton<ITransportStateStore, InMemoryTransportStateStore>();
        services.AddSingleton<ITransportVehicleDriver>(_ => new SimulatorTransportVehicleDriver(TransportVehicleKind.Ems));
        services.AddSingleton<ITransportVehicleDriver>(_ => new SimulatorTransportVehicleDriver(TransportVehicleKind.Rgv));
        services.TryAddSingleton<ITransportDriverResolver, TransportDriverResolver>();
        services.TryAddSingleton<ITransportCommandDispatcher, TransportCommandDispatcher>();
        services.TryAddSingleton<ITransportRecoveryCoordinator, TransportRecoveryCoordinator>();

        services.TryAddSingleton<ITransportChargingCoordinator, TransportChargingCoordinator>();
        services.TryAddSingleton<ITransportTaskReassignmentService, TransportTaskReassignmentService>();
        services.TryAddSingleton<ITransportPerformanceService, TransportPerformanceService>();

        // 第六阶段默认内存实现；Infrastructure 在生产 Host 中替换为 SqlSugar/SQL Server。
        services.TryAddSingleton<ITransportConfigurationStore, InMemoryTransportConfigurationStore>();
        services.TryAddSingleton<ITransportJournalStore, InMemoryTransportJournalStore>();
        services.TryAddSingleton<ITransportGovernanceStore, InMemoryTransportGovernanceStore>();
        services.TryAddSingleton<ITransportConfigurationService, TransportConfigurationService>();
        services.TryAddSingleton<ITransportOperationGovernanceService, TransportOperationGovernanceService>();

        return services;
    }
}
