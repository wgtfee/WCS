namespace Wcs.Core.DeviceCenter;

/// <summary>
/// 设备状态同步器 — 负责从外部源（PLC、StateCenter）同步设备状态
/// </summary>
public class DeviceStateSynchronizer
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceCommandDispatcher _dispatcher;
    private readonly List<IDeviceEventHandler> _eventHandlers = new();
    private readonly object _handlerLock = new();

    public DeviceStateSynchronizer(DeviceRegistry registry, DeviceCommandDispatcher dispatcher)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Subscribe(IDeviceEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlerLock)
        {
            if (!_eventHandlers.Contains(handler))
                _eventHandlers.Add(handler);
        }
    }

    public void Unsubscribe(IDeviceEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlerLock)
        {
            _eventHandlers.Remove(handler);
        }
    }

    /// <summary>
    /// 同步设备状态 — 根据新状态执行对应的操作
    /// </summary>
    public async Task SyncDeviceStateAsync(string deviceId, DeviceStatusEnum newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = _registry.GetDevice(deviceId);
        if (device == null) return;

        var oldStatus = device.Status;
        if (oldStatus == newStatus) return;

        // 状态转移逻辑
        switch (newStatus)
        {
            case DeviceStatusEnum.Running:
                await device.StartAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DeviceStatusEnum.Idle:
                await device.StopAsync(cancellationToken).ConfigureAwait(false);
                break;
            case DeviceStatusEnum.Paused:
                await device.PauseAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        if (device.Status != oldStatus)
        {
            await PublishDeviceStatusChangedAsync(device, oldStatus, newStatus, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishDeviceStatusChangedAsync(IDevice device, DeviceStatusEnum oldStatus,
        DeviceStatusEnum newStatus, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock) { handlers = new List<IDeviceEventHandler>(_eventHandlers); }

        var tasks = handlers.Select(h => h.OnDeviceStatusChangedAsync(device, oldStatus, newStatus, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
