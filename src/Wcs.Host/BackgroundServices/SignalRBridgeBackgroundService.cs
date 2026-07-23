namespace Wcs.Host.BackgroundServices;

using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Infrastructure.SignalR;

/// <summary>
/// 将 StateCenter、任务、报警和物料位置事件桥接到 SignalR。
/// 该服务只负责实时推送，不修改任何生产状态。
/// </summary>
public sealed class SignalRBridgeBackgroundService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly SignalRStatePublisher _publisher;
    private readonly ILogger<SignalRBridgeBackgroundService> _logger;

    public SignalRBridgeBackgroundService(
        IEventBus eventBus,
        SignalRStatePublisher publisher,
        ILogger<SignalRBridgeBackgroundService> logger)
    {
        _eventBus = eventBus;
        _publisher = publisher;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<DeviceStateChangedEvent>(async (evt, ct) =>
        {
            if (evt.DeviceState is not null)
                await _publisher.PushDeviceStateAsync(evt.DeviceId, evt.DeviceState);
        });
        _eventBus.Subscribe<TaskStateChangedEvent>(async (evt, ct) =>
        {
            if (evt.TaskRuntime is not null)
                await _publisher.PushTaskStateAsync(evt.TaskId, evt.TaskRuntime);
        });
        _eventBus.Subscribe<AlarmRaisedEvent>(async (evt, ct) =>
        {
            var payload = evt.AlarmState is null ? (object)evt : evt.AlarmState;
            await _publisher.PushAlarmAsync("Raised", payload);
        });
        _eventBus.Subscribe<AlarmRecoveredEvent>(async (evt, ct) =>
            await _publisher.PushAlarmAsync("Recovered", evt));
        _eventBus.Subscribe<ObjectLocationChangedEvent>(async (evt, ct) =>
            await _publisher.PushObjectLocationAsync(
                evt.ObjectId,
                evt.OldPosition,
                evt.NewPosition));

        _logger.LogInformation("SignalR 事件桥接服务已启动");
        return Task.CompletedTask;
    }
}
