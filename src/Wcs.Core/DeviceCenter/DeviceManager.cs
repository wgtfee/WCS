namespace Wcs.Core.DeviceCenter;

using System.Collections.Concurrent;

/// <summary>
/// 设备管理器接口
/// </summary>
public interface IDeviceManager
{
    /// <summary>
    /// 注册设备
    /// </summary>
    void RegisterDevice(IDevice device);

    /// <summary>
    /// 注销设备
    /// </summary>
    bool UnregisterDevice(string deviceId);

    /// <summary>
    /// 获取指定设备
    /// </summary>
    IDevice? GetDevice(string deviceId);

    /// <summary>
    /// 获取所有设备
    /// </summary>
    IEnumerable<IDevice> GetAllDevices();

    /// <summary>
    /// 按类型获取设备
    /// </summary>
    IEnumerable<IDevice> GetDevicesByType(DeviceTypeEnum type);

    /// <summary>
    /// 启动设备
    /// </summary>
    Task<bool> StartDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止设备
    /// </summary>
    Task<bool> StopDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 复位设备
    /// </summary>
    Task<bool> ResetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 暂停设备
    /// </summary>
    Task<bool> PauseDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复设备
    /// </summary>
    Task<bool> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设备计数
    /// </summary>
    int GetTotalDeviceCount();

    /// <summary>
    /// 获取指定状态的设备数
    /// </summary>
    int GetDeviceCountByStatus(DeviceStatusEnum status);

    /// <summary>
    /// 订阅设备事件
    /// </summary>
    void Subscribe(IDeviceEventHandler handler);

    /// <summary>
    /// 取消订阅设备事件
    /// </summary>
    void Unsubscribe(IDeviceEventHandler handler);

    /// <summary>
    /// 同步设备状态
    /// </summary>
    Task SyncDeviceStateAsync(string deviceId, DeviceStatusEnum newStatus, CancellationToken cancellationToken = default);
}

/// <summary>
/// 设备管理器实现
/// </summary>
public class DeviceManager : IDeviceManager
{
    private readonly ConcurrentDictionary<string, IDevice> _devices = new();
    private readonly List<IDeviceEventHandler> _eventHandlers = new();
    private readonly object _handlerLock = new();

    public void RegisterDevice(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!_devices.TryAdd(device.DeviceId, device))
        {
            throw new InvalidOperationException($"Device {device.DeviceId} already registered");
        }
    }

    public bool UnregisterDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _devices.TryRemove(deviceId, out _);
    }

    public IDevice? GetDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _devices.TryGetValue(deviceId, out var device);
        return device;
    }

    public IEnumerable<IDevice> GetAllDevices()
    {
        return _devices.Values.ToList();
    }

    public IEnumerable<IDevice> GetDevicesByType(DeviceTypeEnum type)
    {
        return _devices.Values
            .Where(d => d.DeviceType == type)
            .ToList();
    }

    public async Task<bool> StartDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return false;

        var oldStatus = device.Status;
        var result = await device.StartAsync(cancellationToken).ConfigureAwait(false);

        if (result && device.Status != oldStatus)
        {
            await PublishDeviceStartedAsync(device, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<bool> StopDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return false;

        var oldStatus = device.Status;
        var result = await device.StopAsync(cancellationToken).ConfigureAwait(false);

        if (result && device.Status != oldStatus)
        {
            await PublishDeviceStoppedAsync(device, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<bool> ResetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return false;

        return await device.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PauseDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return false;

        return await device.PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return false;

        return await device.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public int GetTotalDeviceCount()
    {
        return _devices.Count;
    }

    public int GetDeviceCountByStatus(DeviceStatusEnum status)
    {
        return _devices.Values.Count(d => d.Status == status);
    }

    public void Subscribe(IDeviceEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_handlerLock)
        {
            if (!_eventHandlers.Contains(handler))
            {
                _eventHandlers.Add(handler);
            }
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

    public async Task SyncDeviceStateAsync(string deviceId, DeviceStatusEnum newStatus, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var device = GetDevice(deviceId);
        if (device == null)
            return;

        var oldStatus = device.Status;

        if (oldStatus == newStatus)
            return;

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
            await PublishDeviceStatusChangedAsync(device, oldStatus, newStatus, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 获取事件处理器数量
    /// </summary>
    public int GetEventHandlerCount()
    {
        lock (_handlerLock)
        {
            return _eventHandlers.Count;
        }
    }

    /// <summary>
    /// 发布设备启动事件
    /// </summary>
    private async Task PublishDeviceStartedAsync(IDevice device, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock)
        {
            handlers = new List<IDeviceEventHandler>(_eventHandlers);
        }

        var tasks = handlers.Select(h => h.OnDeviceStartedAsync(device, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 发布设备停止事件
    /// </summary>
    private async Task PublishDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock)
        {
            handlers = new List<IDeviceEventHandler>(_eventHandlers);
        }

        var tasks = handlers.Select(h => h.OnDeviceStoppedAsync(device, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 发布设备状态变化事件
    /// </summary>
    private async Task PublishDeviceStatusChangedAsync(IDevice device, DeviceStatusEnum oldStatus, DeviceStatusEnum newStatus, CancellationToken cancellationToken)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock)
        {
            handlers = new List<IDeviceEventHandler>(_eventHandlers);
        }

        var tasks = handlers.Select(h => h.OnDeviceStatusChangedAsync(device, oldStatus, newStatus, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 发布设备错误事件
    /// </summary>
    public async Task PublishDeviceErrorAsync(IDevice device, string errorMessage, CancellationToken cancellationToken = default)
    {
        List<IDeviceEventHandler> handlers;
        lock (_handlerLock)
        {
            handlers = new List<IDeviceEventHandler>(_eventHandlers);
        }

        var tasks = handlers.Select(h => h.OnDeviceErrorAsync(device, errorMessage, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
