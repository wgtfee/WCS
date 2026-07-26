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
    /// 现场联调、生产调度、可观测性、生产韧性，以及第十二阶段离线仿真、优化和最终验收组件。
    /// </summary>
    public static IServiceCollection AddUnifiedTransportScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new TransportObservabilityOptions());
        services.TryAddSingleton(new TransportResilienceOptions());
        services.TryAddSingleton(new TransportSimulationOptions());
        services.TryAddSingleton(new TransportCycleAnalysisOptions());
        services.TryAddSingleton<TransportCycleAnalysisService>();
        services.TryAddSingleton<ITransportCycleAnalysisService>(sp =>
            sp.GetRequiredService<TransportCycleAnalysisService>());
        services.TryAddSingleton<TransportTelemetryService>();
        services.TryAddSingleton<ITransportTelemetryService>(sp =>
            sp.GetRequiredService<TransportTelemetryService>());

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
        services.TryAddSingleton<ITransportLogicalBackupStorage, InMemoryTransportLogicalBackupStorage>();

        services.TryAddSingleton<ITransportProductionTuningService, TransportProductionTuningService>();
        services.TryAddSingleton<ITransportStationCongestionService, TransportStationCongestionService>();
        services.TryAddSingleton<ITransportSingleTrackCoordinator, TransportSingleTrackCoordinator>();
        services.AddSingleton<ITransportDispatchAdmissionPolicy, TransportSingleTrackDispatchAdmissionPolicy>();
        services.TryAddSingleton<JournalTransportDispatchDecisionStore>();
        services.TryAddSingleton<ITransportDispatchDecisionStore>(sp =>
            sp.GetRequiredService<JournalTransportDispatchDecisionStore>());
        services.TryAddSingleton<ITransportDynamicPriorityService, TransportDynamicPriorityService>();

        services.TryAddSingleton<InMemoryRouteReservationManager>();
        services.TryAddSingleton<IRouteReservationManager, TrafficAwareRouteReservationManager>();
        services.TryAddSingleton<UnifiedTransportDispatchEngine>();
        services.TryAddSingleton<ObservableUnifiedTransportDispatchEngine>();
        services.TryAddSingleton<IUnifiedTransportDispatchEngine>(sp =>
            sp.GetRequiredService<ObservableUnifiedTransportDispatchEngine>());
        services.TryAddSingleton<ReliableTransportProductionDispatchService>();
        services.TryAddSingleton<ObservableTransportProductionDispatchService>();
        services.TryAddSingleton<ITransportProductionDispatchService>(sp =>
            sp.GetRequiredService<ObservableTransportProductionDispatchService>());
        services.TryAddSingleton<InMemoryTransportExecutionEngine>();
        services.TryAddSingleton<CoordinatedTransportExecutionEngine>();
        services.TryAddSingleton<ObservedTransportExecutionEngine>();
        services.TryAddSingleton<ITransportExecutionEngine>(sp =>
            sp.GetRequiredService<ObservedTransportExecutionEngine>());
        services.TryAddSingleton<ITransportReassignmentExecutionControl>(sp =>
            sp.GetRequiredService<ObservedTransportExecutionEngine>());
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
        services.TryAddSingleton<TransportCommandDispatcher>();
        services.TryAddSingleton<ObservableTransportCommandDispatcher>();
        services.TryAddSingleton<ITransportCommandDispatcher>(sp =>
            sp.GetRequiredService<ObservableTransportCommandDispatcher>());
        services.TryAddSingleton<ITransportRecoveryCoordinator, TransportRecoveryCoordinator>();
        services.TryAddSingleton<ITransportDriverSynchronizationService, TransportDriverSynchronizationService>();

        services.TryAddSingleton<ITransportChargingCoordinator, TransportChargingCoordinator>();
        services.TryAddSingleton<ITransportTaskReassignmentService, TransportTaskReassignmentService>();
        services.TryAddSingleton<ITransportPerformanceService, TransportPerformanceService>();
        services.TryAddSingleton<SafeTransportFaultTakeoverService>();
        services.TryAddSingleton<ITransportFaultTakeoverService>(sp =>
            sp.GetRequiredService<SafeTransportFaultTakeoverService>());
        services.TryAddSingleton<ITransportProductionTrendService, TransportProductionTrendService>();

        services.TryAddSingleton<ITransportConfigurationService, TransportConfigurationService>();
        services.TryAddSingleton<ITransportOperationGovernanceService, TransportOperationGovernanceService>();
        services.TryAddSingleton<ITransportPlcSignalMapService, TransportPlcSignalMapService>();
        services.TryAddSingleton<ITransportConfigurationSnapshotService, TransportConfigurationSnapshotService>();

        services.TryAddSingleton<ITransportPointTableImporter, TransportPointTableImporter>();
        services.TryAddSingleton<ITransportSignalTemplateService, TransportSignalTemplateService>();
        services.TryAddSingleton<ITransportCommissioningService, TransportCommissioningService>();
        services.TryAddSingleton<ITransportFaultCatalogService, TransportFaultCatalogService>();
        services.TryAddSingleton<ITransportRecoveryConflictService, TransportRecoveryConflictService>();
        services.TryAddSingleton<ITransportCommandCompensationService, TransportCommandCompensationService>();

        services.TryAddSingleton<ITransportConsistencyInspectionService, TransportConsistencyInspectionService>();
        services.TryAddSingleton<ITransportObservabilityService, TransportObservabilityService>();
        services.TryAddSingleton<ITransportResilienceService, TransportResilienceService>();
        services.TryAddSingleton<TransportSimulationService>();
        services.TryAddSingleton<SafeTransportSimulationService>();
        services.TryAddSingleton<ITransportSimulationService>(sp =>
            sp.GetRequiredService<SafeTransportSimulationService>());

        return services;
    }
}
