namespace Wcs.Core.DeviceCenter;

using System.Collections.Concurrent;

/// <summary>
/// 设备命令调度器 — 负责设备的启动、停止、复位、暂停、恢复等操作
/// </summary>
public class DeviceCommandDispatcher
{
    private readonly DeviceRegistry _registry;
    private readonly List<IDeviceEventHandler> _eventHandlers = new();
    private readonly object _handlerLock = new();

    public DeviceCommandDispatcher(DeviceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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

    public int GetHandlerCount()
    {
        lock (_handlerLock) { return _eventHandlers.Count; }
    }

    public async Task<bool> StartDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = _registry.GetDevice(deviceId);
        if (device == null) return false;

        var oldStatus = device.Status;
        var result = await device.StartAsync(cancellationToken).ConfigureAwait(false);

        if (result && device.Status != oldStatus)
            await PublishDeviceStartedAsync(device, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<bool> StopDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = _registry.GetDevice(deviceId);
        if (device == null) return false;

        var oldStatus = device.Status;
        var result = await device.StopAsync(cancellationToken).ConfigureAwait(false);

        if (result && device.Status != oldStatus)
            await PublishDeviceStoppedAsync(device, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<bool> ResetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = _registry.GetDevice(deviceId);
        if (device == null) return false;
        return await device.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PauseDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = _registry.GetDevice(deviceId);
        if (device == null) return false;
        return await device.PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = _registry.GetDevice(deviceId);
        if (device == null) return false;
        return await device.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishDeviceErrorAsync(IDevice device, string errorMessage, CancellationToken cancellationToken = default)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock) { handlers = new List<IDeviceEventHandler>(_eventHandlers); }

        var tasks = handlers.Select(h => h.OnDeviceErrorAsync(device, errorMessage, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PublishDeviceStartedAsync(IDevice device, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock) { handlers = new List<IDeviceEventHandler>(_eventHandlers); }

        var tasks = handlers.Select(h => h.OnDeviceStartedAsync(device, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PublishDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock) { handlers = new List<IDeviceEventHandler>(_eventHandlers); }

        var tasks = handlers.Select(h => h.OnDeviceStoppedAsync(device, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
