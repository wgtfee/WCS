namespace Wcs.Application;

using Wcs.Application.HostedServices;
using Wcs.Application.Services;
using Wcs.Core.AlarmCenter;
using Wcs.Core.CommandCenter;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Persistence;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.ObjectTracking;
using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.Recovery;
using Wcs.Core.ResourceLock;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.TaskEngine.Chain;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;
using Wcs.Core.TransportScheduling;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.StateCenter.Implementation;
using Wcs.Core.EventBus.Events;

public static class DependencyInjection
{
    public static IServiceCollection AddWcsApplication(this IServiceCollection services)
    {
        services.AddSingleton<StateCenter>(_ => new StateCenter());
        services.AddSingleton<IStateCenter>(sp => sp.GetRequiredService<StateCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<StateCenter>());
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IIdempotencyManager, IdempotencyManager>();
        services.AddSingleton<ITaskScheduler, TaskScheduler>();
        services.AddSingleton<IResourceLockManager, ResourceLockManager>();

        services.AddUnifiedTransportScheduling();
        services.AddHostedService<TransportConfigurationHostedService>();
        services.AddHostedService<TransportPlcSignalMapHostedService>();
        services.AddHostedService<TransportDriverReconciliationHostedService>();
        services.AddHostedService<TransportRecoveryConflictHostedService>();
        services.AddHostedService<TransportDriverPollingHostedService>();
        services.AddHostedService<TransportOptimizationHostedService>();
        services.AddHostedService<TransportJournalHostedService>();
        services.AddHostedService<TransportFaultAlarmHostedService>();
        services.AddHostedService<TransportProductionConfigurationHostedService>();
        services.AddHostedService<TransportDispatchDecisionRestoreHostedService>();
        services.AddHostedService<TransportProductionDispatchHostedService>();
        services.AddHostedService<TransportProductionTrendHostedService>();
        services.AddHostedService<TransportFaultTakeoverHostedService>();

        services.AddSingleton<IEventStore, FileEventStore>();
        services.AddSingleton<EventReplayService>();

        services.AddSingleton<AlarmCenter>(sp => new AlarmCenter(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<IAlarmCenter>(sp => sp.GetRequiredService<AlarmCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<AlarmCenter>());

        services.AddSingleton<ObjectTrackingCenter>(sp =>
        {
            var center = new ObjectTrackingCenter(sp.GetRequiredService<IEventBus>());
            center.SetTopologyGraph(sp.GetRequiredService<TopologyGraph>());
            return center;
        });
        services.AddSingleton<IObjectTrackingCenter>(sp => sp.GetRequiredService<ObjectTrackingCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<ObjectTrackingCenter>());
        services.AddSingleton<DeadlockDetector>();

        services.AddSingleton<ISnapshotRepository, SnapshotRepository>();
        services.AddSingleton<IRecoveryManager>(sp => new RecoveryManager(
            sp.GetServices<ISnapshotProvider>(), sp.GetRequiredService<ISnapshotRepository>()));

        services.AddSingleton<ITaskOrchestrator>(sp => new TaskOrchestrator(
            sp.GetRequiredService<IStateCenter>(), sp.GetRequiredService<ITaskScheduler>()));
        services.AddSingleton<ITaskChainEngine>(sp => new TaskChainEngine(
            sp.GetRequiredService<ITaskOrchestrator>(), sp.GetRequiredService<ITaskScheduler>()));

        services.AddSingleton<PlcWriter>();
        services.AddSingleton<ICommandCenter, CommandCenter>();
        services.AddSingleton<WcsApplicationService>();
        services.AddHostedService<EventBusSubscriberHostedService>();

        return services;
    }
}

internal sealed class TransportOptimizationHostedService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ITransportChargingCoordinator _charging;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    public TransportOptimizationHostedService(ITransportChargingCoordinator charging)
    {
        _charging = charging;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _charging.EvaluateFleet();
            }
            catch
            {
                // 单次调度评估失败不应终止整个 WCS Host。
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

internal class EventBusSubscriberHostedService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly WcsApplicationService _appService;

    public EventBusSubscriberHostedService(IEventBus eventBus, WcsApplicationService appService)
    {
        _eventBus = eventBus;
        _appService = appService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<TaskCompletedEvent>(async (evt, ct) => await Task.CompletedTask);
        _eventBus.Subscribe<AlarmRaisedEvent>(async (evt, ct) => await Task.CompletedTask);
        await Task.CompletedTask;
    }
}
