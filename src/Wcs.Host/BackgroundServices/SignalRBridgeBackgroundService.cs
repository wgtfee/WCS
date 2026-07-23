namespace Wcs.Host.BackgroundServices;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;
using Wcs.Infrastructure.SignalR;

/// <summary>
/// 将 StateCenter、任务、报警和物料位置事件桥接到 SignalR。
/// 该服务只负责实时推送，不修改任何生产状态。
/// </summary>
public sealed class SignalRBridgeBackgroundService : BackgroundService
{
    private static readonly TimeSpan DeviceFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IEventBus _eventBus;
    private readonly SignalRStatePublisher _publisher;
    private readonly ILogger<SignalRBridgeBackgroundService> _logger;
    private readonly ConcurrentDictionary<string, DeviceState> _pendingDeviceStates = new();

    public SignalRBridgeBackgroundService(
        IEventBus eventBus,
        SignalRStatePublisher publisher,
        ILogger<SignalRBridgeBackgroundService> logger)
    {
        _eventBus = eventBus;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PLC polling can change the same device many times before a WebSocket
        // client consumes the previous update. Keep only the latest state per
        // device so slow clients cannot create an unbounded number of send tasks.
        _eventBus.Subscribe<DeviceStateChangedEvent>((evt, _) =>
        {
            if (evt.DeviceState is not null)
                _pendingDeviceStates[evt.DeviceId] = evt.DeviceState;
            return Task.CompletedTask;
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

        _logger.LogInformation(
            "SignalR 事件桥接服务已启动 — 设备状态按 {Interval}ms 合并刷新",
            DeviceFlushInterval.TotalMilliseconds);

        using var timer = new PeriodicTimer(DeviceFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushDeviceStatesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task FlushDeviceStatesAsync(CancellationToken stoppingToken)
    {
        foreach (var deviceId in _pendingDeviceStates.Keys)
        {
            if (!_pendingDeviceStates.TryRemove(deviceId, out var state))
                continue;

            try
            {
                await _publisher.PushDeviceStateAsync(deviceId, state);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR 设备状态推送失败: {DeviceId}", deviceId);
            }
        }
    }
}
