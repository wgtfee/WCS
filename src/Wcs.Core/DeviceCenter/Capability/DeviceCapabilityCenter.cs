namespace Wcs.Core.DeviceCenter.Capability;

using System.Collections.Concurrent;

/// <summary>
/// 设备能力中心 — 统一管理设备能力注册、查询、匹配
///
/// 使用场景：任务只需声明「我要一个能存储的设备」，
/// DeviceCapabilityCenter 返回所有具备 CanStore 能力的设备，
/// 路由中心或任务编排器自行选择具体设备。
/// </summary>
public class DeviceCapabilityCenter : IDeviceCapabilityCenter
{
    private readonly ConcurrentDictionary<string, DeviceCapabilityRecord> _records = new();

    // 能力 → 设备ID集合 倒排索引
    private readonly ConcurrentDictionary<DeviceCapability, HashSet<string>> _capabilityIndex = new();
    private readonly object _lock = new();

    public void RegisterCapability(string deviceId, DeviceCapability capabilities)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var record = new DeviceCapabilityRecord
        {
            DeviceId = deviceId,
            Capabilities = capabilities
        };

        _records[deviceId] = record;
        RebuildIndex();
    }

    public void RegisterCapabilityRecord(DeviceCapabilityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records[record.DeviceId] = record;
        RebuildIndex();
    }

    public DeviceCapability? GetCapability(string deviceId)
    {
        return _records.TryGetValue(deviceId, out var record) ? record.Capabilities : null;
    }

    public DeviceCapabilityRecord? GetCapabilityRecord(string deviceId)
    {
        _records.TryGetValue(deviceId, out var record);
        return record;
    }

    public IEnumerable<string> FindDevices(DeviceCapability requiredCapability)
    {
        return _records.Values
            .Where(r => (r.Capabilities & requiredCapability) == requiredCapability)
            .Select(r => r.DeviceId);
    }

    public IEnumerable<string> FindDevicesAll(DeviceCapability requiredCapabilities)
    {
        return _records.Values
            .Where(r => (r.Capabilities & requiredCapabilities) == requiredCapabilities)
            .Select(r => r.DeviceId);
    }

    public bool HasCapability(string deviceId, DeviceCapability capability)
    {
        return _records.TryGetValue(deviceId, out var record) &&
               (record.Capabilities & capability) == capability;
    }

    public bool RemoveCapability(string deviceId)
    {
        if (_records.TryRemove(deviceId, out _))
        {
            RebuildIndex();
            return true;
        }
        return false;
    }

    public IReadOnlyList<DeviceCapabilityRecord> GetAllCapabilities()
    {
        return _records.Values.ToList();
    }

    public DeviceCapabilityStats GetStats()
    {
        var all = _records.Values;
        return new DeviceCapabilityStats
        {
            TotalDevices = all.Count,
            Conveyors = all.Count(r => r.Capabilities.HasFlag(DeviceCapability.CanConvey)),
            Lifts = all.Count(r => r.Capabilities.HasFlag(DeviceCapability.CanLift)),
            Storages = all.Count(r => r.Capabilities.HasFlag(DeviceCapability.CanStore)),
            Robots = all.Count(r => r.Capabilities.HasFlag(DeviceCapability.CanGrip)),
            Sorters = all.Count(r => r.Capabilities.HasFlag(DeviceCapability.CanSort))
        };
    }

    private void RebuildIndex()
    {
        lock (_lock)
        {
            _capabilityIndex.Clear();
            foreach (var (_, record) in _records)
            {
                var caps = record.Capabilities;
                foreach (DeviceCapability cap in Enum.GetValues<DeviceCapability>())
                {
                    if (cap != DeviceCapability.None && caps.HasFlag(cap))
                    {
                        var ids = _capabilityIndex.GetOrAdd(cap, _ => new HashSet<string>());
                        ids.Add(record.DeviceId);
                    }
                }
            }
        }
    }
}
