namespace Wcs.Application;

using Wcs.Application.Services;
using Wcs.Core.AlarmCenter;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.ObjectTracking;
using Wcs.Core.Recovery;
using Wcs.Core.ResourceLock;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.TaskEngine.Chain;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.StateCenter.Implementation;
using Wcs.Core.EventBus.Events;

/// <summary>
/// WCS Application 层 DI 注册
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddWcsApplication(this IServiceCollection services)
    {
        // Core singletons
        // StateCenter: 先注册实例，EventBus 是可选依赖
        services.AddSingleton<StateCenter>(_ => new StateCenter());
        services.AddSingleton<IStateCenter>(sp => sp.GetRequiredService<StateCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<StateCenter>());
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IIdempotencyManager, IdempotencyManager>();
        services.AddSingleton<ITaskScheduler, TaskScheduler>();
        services.AddSingleton<IResourceLockManager, ResourceLockManager>();

        // Core - depends on above
        services.AddSingleton<AlarmCenter>(sp =>
            new AlarmCenter(sp.GetRequiredService<IEventBus>()));
        services.AddSingleton<IAlarmCenter>(sp => sp.GetRequiredService<AlarmCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<AlarmCenter>());

        services.AddSingleton<ObjectTrackingCenter>();
        services.AddSingleton<IObjectTrackingCenter>(sp => sp.GetRequiredService<ObjectTrackingCenter>());
        services.AddSingleton<ISnapshotProvider>(sp => sp.GetRequiredService<ObjectTrackingCenter>());
        services.AddSingleton<DeadlockDetector>();

        // Recovery
        services.AddSingleton<ISnapshotRepository, SnapshotRepository>();
        services.AddSingleton<IRecoveryManager>(sp =>
            new RecoveryManager(
                sp.GetServices<ISnapshotProvider>(),
                sp.GetRequiredService<ISnapshotRepository>()));

        // Task engine
        services.AddSingleton<ITaskOrchestrator>(sp =>
            new TaskOrchestrator(
                sp.GetRequiredService<IStateCenter>(),
                sp.GetRequiredService<ITaskScheduler>()));
        services.AddSingleton<ITaskChainEngine>(sp =>
            new TaskChainEngine(
                sp.GetRequiredService<ITaskOrchestrator>(),
                sp.GetRequiredService<ITaskScheduler>()));

        // Application service
        services.AddSingleton<WcsApplicationService>();

        // Register EventBus subscriptions
        services.AddHostedService<EventBusSubscriberHostedService>();

        return services;
    }
}

/// <summary>
/// 事件总线订阅者注册 - 确保 EventBus 在应用启动时完成订阅
/// </summary>
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
        // 订阅事件: 任务完成时自动归档
        _eventBus.Subscribe<TaskCompletedEvent>(async (evt, ct) =>
        {
            // 扩展点: 持久化到数据库等
            await Task.CompletedTask;
        });

        // 订阅报警事件
        _eventBus.Subscribe<AlarmRaisedEvent>(async (evt, ct) =>
        {
            // 扩展点: 报警通知、日志等
            await Task.CompletedTask;
        });

        await Task.CompletedTask;
    }
}
