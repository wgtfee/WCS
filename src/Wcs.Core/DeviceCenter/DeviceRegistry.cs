namespace Wcs.Core.DeviceCenter;

using System.Collections.Concurrent;

/// <summary>
/// 设备注册表 — 负责设备的注册、注销、查询
/// </summary>
public class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, IDevice> _devices = new();

    /// <summary>
    /// 注册设备
    /// </summary>
    public void RegisterDevice(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_devices.TryAdd(device.DeviceId, device))
            throw new InvalidOperationException($"Device {device.DeviceId} already registered");
    }

    /// <summary>
    /// 注销设备
    /// </summary>
    public bool UnregisterDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _devices.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// 获取指定设备
    /// </summary>
    public IDevice? GetDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _devices.TryGetValue(deviceId, out var device);
        return device;
    }

    /// <summary>
    /// 获取所有设备
    /// </summary>
    public IEnumerable<IDevice> GetAllDevices()
    {
        return _devices.Values.ToList();
    }

    /// <summary>
    /// 按类型获取设备
    /// </summary>
    public IEnumerable<IDevice> GetDevicesByType(DeviceTypeEnum type)
    {
        return _devices.Values.Where(d => d.DeviceType == type).ToList();
    }

    /// <summary>
    /// 设备计数
    /// </summary>
    public int Count => _devices.Count;

    /// <summary>
    /// 获取指定状态的设备数
    /// </summary>
    public int GetCountByStatus(DeviceStatusEnum status)
    {
        return _devices.Values.Count(d => d.Status == status);
    }

    /// <summary>
    /// 设备是否存在
    /// </summary>
    public bool Exists(string deviceId)
    {
        return _devices.ContainsKey(deviceId);
    }
}
