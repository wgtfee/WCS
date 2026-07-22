namespace Wcs.Core.TransportScheduling;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;

public static class TransportSchedulingRegistrationExtensions
{
    /// <summary>
    /// 注册 EMS/RGV 统一调度、执行、恢复、交通控制、充电、治理、PLC 驱动、
    /// 现场联调，以及第九阶段生产竞争队列、站点拥堵、单轨会车、趋势和故障接管组件。
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

        services.TryAddSingleton<ITransportConfigurationStore, InMemoryTransportConfigurationStore>();
        services.TryAddSingleton<ITransportJournalStore, InMemoryTransportJournalStore>();
        services.TryAddSingleton<ITransportGovernanceStore, InMemoryTransportGovernanceStore>();
        services.TryAddSingleton<ITransportPlcSignalMapStore, InMemoryTransportPlcSignalMapStore>();
        services.TryAddSingleton<ITransportCommissioningStore, InMemoryTransportCommissioningStore>();

        services.TryAddSingleton<ITransportProductionTuningService, TransportProductionTuningService>();
        services.TryAddSingleton<ITransportStationCongestionService, TransportStationCongestionService>();
        services.TryAddSingleton<ITransportSingleTrackCoordinator, TransportSingleTrackCoordinator>();
        services.AddSingleton<ITransportDispatchAdmissionPolicy, TransportSingleTrackDispatchAdmissionPolicy>();
        services.TryAddSingleton<ITransportDispatchDecisionStore, InMemoryTransportDispatchDecisionStore>();
        services.TryAddSingleton<ITransportDynamicPriorityService, TransportDynamicPriorityService>();

        services.TryAddSingleton<InMemoryRouteReservationManager>();
        services.TryAddSingleton<IRouteReservationManager, TrafficAwareRouteReservationManager>();
        services.TryAddSingleton<IUnifiedTransportDispatchEngine, UnifiedTransportDispatchEngine>();
        services.TryAddSingleton<ITransportProductionDispatchService, TransportProductionDispatchService>();
        services.TryAddSingleton<InMemoryTransportExecutionEngine>();
        services.TryAddSingleton<CoordinatedTransportExecutionEngine>();
        services.TryAddSingleton<ITransportExecutionEngine>(sp =>
            sp.GetRequiredService<CoordinatedTransportExecutionEngine>());
        services.TryAddSingleton<ITransportReassignmentExecutionControl>(sp =>
            sp.GetRequiredService<CoordinatedTransportExecutionEngine>());
        services.TryAddSingleton<ITransportDeadlockService, TransportDeadlockService>();

        services.TryAddSingleton<ITransportStateStore, InMemoryTransportStateStore>();
        services.TryAddSingleton<ITransportPlcSignalMapRegistry, InMemoryTransportPlcSignalMapRegistry>();
        services.TryAddSingleton<ITransportDriverDiagnosticsService, TransportDriverDiagnosticsService>();
        services.TryAddSingleton<ITransportCommunicationTraceStore, InMemoryTransportCommunicationTraceStore>();
        services.TryAddSingleton<InMemoryTransportPlcAccessor>();
        services.TryAddSingleton<HybridTransportPlcAccessor>();
        services.TryAddSingleton<TransportObservedPlcAccessor>();
        services.TryAddSingleton<ITransportPlcAccessor>(sp =>
            sp.GetRequiredService<TransportObservedPlcAccessor>());
        services.TryAddSingleton<TransportPlcDriverChannel>();
        services.TryAddSingleton<ITransportDriverChannel>(sp =>
            sp.GetRequiredService<TransportPlcDriverChannel>());

        services.AddSingleton<ITransportVehicleDriver>(sp =>
            new SwitchableTransportVehicleDriver(
                TransportVehicleKind.Ems,
                sp.GetRequiredService<ITransportPlcSignalMapRegistry>(),
                sp.GetRequiredService<ITransportDriverChannel>()));
        services.AddSingleton<ITransportVehicleDriver>(sp =>
            new SwitchableTransportVehicleDriver(
                TransportVehicleKind.Rgv,
                sp.GetRequiredService<ITransportPlcSignalMapRegistry>(),
                sp.GetRequiredService<ITransportDriverChannel>()));
        services.TryAddSingleton<ITransportDriverResolver, TransportDriverResolver>();
        services.TryAddSingleton<ITransportCommandDispatcher, TransportCommandDispatcher>();
        services.TryAddSingleton<ITransportRecoveryCoordinator, TransportRecoveryCoordinator>();
        services.TryAddSingleton<ITransportDriverSynchronizationService, TransportDriverSynchronizationService>();

        services.TryAddSingleton<ITransportChargingCoordinator, TransportChargingCoordinator>();
        services.TryAddSingleton<ITransportTaskReassignmentService, TransportTaskReassignmentService>();
        services.TryAddSingleton<ITransportPerformanceService, TransportPerformanceService>();
        services.TryAddSingleton<ITransportFaultTakeoverService, TransportFaultTakeoverService>();
        services.TryAddSingleton<ITransportProductionTrendService, TransportProductionTrendService>();

        services.TryAddSingleton<ITransportConfigurationService, TransportConfigurationService>();
        services.TryAddSingleton<ITransportOperationGovernanceService, TransportOperationGovernanceService>();
        services.TryAddSingleton<ITransportPlcSignalMapService, TransportPlcSignalMapService>();

        services.TryAddSingleton<ITransportPointTableImporter, TransportPointTableImporter>();
        services.TryAddSingleton<ITransportSignalTemplateService, TransportSignalTemplateService>();
        services.TryAddSingleton<ITransportCommissioningService, TransportCommissioningService>();
        services.TryAddSingleton<ITransportFaultCatalogService, TransportFaultCatalogService>();
        services.TryAddSingleton<ITransportRecoveryConflictService, TransportRecoveryConflictService>();
        services.TryAddSingleton<ITransportCommandCompensationService, TransportCommandCompensationService>();

        return services;
    }
}
