namespace Wcs.Core.DeviceCenter;

/// <summary>
/// 设备管理器接口
/// </summary>
public interface IDeviceManager
{
    void RegisterDevice(IDevice device);
    bool UnregisterDevice(string deviceId);
    IDevice? GetDevice(string deviceId);
    IEnumerable<IDevice> GetAllDevices();
    IEnumerable<IDevice> GetDevicesByType(DeviceTypeEnum type);
    Task<bool> StartDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> StopDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> ResetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> PauseDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    int GetTotalDeviceCount();
    int GetDeviceCountByStatus(DeviceStatusEnum status);
    void Subscribe(IDeviceEventHandler handler);
    void Unsubscribe(IDeviceEventHandler handler);
    Task SyncDeviceStateAsync(string deviceId, DeviceStatusEnum newStatus, CancellationToken cancellationToken = default);
}

/// <summary>
/// 设备管理器实现 — 委托给 4 个子组件：
/// DeviceRegistry + DeviceCommandDispatcher + DeviceStateSynchronizer + DeviceHealthMonitor
/// </summary>
public class DeviceManager : IDeviceManager
{
    /// <summary>设备注册表</summary>
    public DeviceRegistry Registry { get; }

    /// <summary>设备命令调度器</summary>
    public DeviceCommandDispatcher CommandDispatcher { get; }

    /// <summary>设备状态同步器</summary>
    public DeviceStateSynchronizer StateSynchronizer { get; }

    /// <summary>设备健康监控器</summary>
    public DeviceHealthMonitor HealthMonitor { get; }

    public DeviceManager()
    {
        Registry = new DeviceRegistry();
        CommandDispatcher = new DeviceCommandDispatcher(Registry);
        StateSynchronizer = new DeviceStateSynchronizer(Registry, CommandDispatcher);
        HealthMonitor = new DeviceHealthMonitor(Registry, CommandDispatcher);
    }

    // ==================== 设备注册/注销/查询（委托给 Registry） ====================

    public void RegisterDevice(IDevice device) => Registry.RegisterDevice(device);

    public bool UnregisterDevice(string deviceId) => Registry.UnregisterDevice(deviceId);

    public IDevice? GetDevice(string deviceId) => Registry.GetDevice(deviceId);

    public IEnumerable<IDevice> GetAllDevices() => Registry.GetAllDevices();

    public IEnumerable<IDevice> GetDevicesByType(DeviceTypeEnum type) => Registry.GetDevicesByType(type);

    public int GetTotalDeviceCount() => Registry.Count;

    public int GetDeviceCountByStatus(DeviceStatusEnum status) => Registry.GetCountByStatus(status);

    // ==================== 设备命令（委托给 CommandDispatcher） ====================

    public Task<bool> StartDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => CommandDispatcher.StartDeviceAsync(deviceId, cancellationToken);

    public Task<bool> StopDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => CommandDispatcher.StopDeviceAsync(deviceId, cancellationToken);

    public Task<bool> ResetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => CommandDispatcher.ResetDeviceAsync(deviceId, cancellationToken);

    public Task<bool> PauseDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => CommandDispatcher.PauseDeviceAsync(deviceId, cancellationToken);

    public Task<bool> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        => CommandDispatcher.ResumeDeviceAsync(deviceId, cancellationToken);

    // ==================== 设备事件（委托给 CommandDispatcher） ====================

    public void Subscribe(IDeviceEventHandler handler) => CommandDispatcher.Subscribe(handler);

    public void Unsubscribe(IDeviceEventHandler handler) => CommandDispatcher.Unsubscribe(handler);

    // ==================== 状态同步（委托给 StateSynchronizer） ====================

    public Task SyncDeviceStateAsync(string deviceId, DeviceStatusEnum newStatus,
        CancellationToken cancellationToken = default)
        => StateSynchronizer.SyncDeviceStateAsync(deviceId, newStatus, cancellationToken);
}
