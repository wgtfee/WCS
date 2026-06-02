namespace Wcs.Core.DeviceCenter;

using System.Collections.Concurrent;

/// <summary>
/// 设备健康监控器 — 心跳检测、健康状态、自动恢复
/// </summary>
public class DeviceHealthMonitor : IDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceCommandDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, DeviceHealthRecord> _healthRecords = new();
    private Timer? _heartbeatTimer;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _timeoutThreshold;
    private bool _disposed;

    /// <summary>
    /// 设备健康记录
    /// </summary>
    public class DeviceHealthRecord
    {
        public string DeviceId { get; set; } = string.Empty;
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public int FailureCount { get; set; }
        public bool IsHealthy { get; set; } = true;
        public string? LastError { get; set; }
    }

    public DeviceHealthMonitor(
        DeviceRegistry registry,
        DeviceCommandDispatcher dispatcher,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? timeoutThreshold = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
        _timeoutThreshold = timeoutThreshold ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 开始健康监控
    /// </summary>
    public void Start()
    {
        _heartbeatTimer = new Timer(CheckHealth, null, _heartbeatInterval, _heartbeatInterval);
    }

    /// <summary>
    /// 停止健康监控
    /// </summary>
    public void Stop()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// 记录设备心跳
    /// </summary>
    public void RecordHeartbeat(string deviceId)
    {
        var record = _healthRecords.GetOrAdd(deviceId, _ => new DeviceHealthRecord
        {
            DeviceId = deviceId,
            LastHeartbeat = DateTime.UtcNow,
            IsHealthy = true
        });
        record.LastHeartbeat = DateTime.UtcNow;
        record.IsHealthy = true;
    }

    /// <summary>
    /// 记录设备故障
    /// </summary>
    public void RecordFailure(string deviceId, string? error = null)
    {
        var record = _healthRecords.GetOrAdd(deviceId, _ => new DeviceHealthRecord
        {
            DeviceId = deviceId,
            LastHeartbeat = DateTime.UtcNow,
            IsHealthy = false
        });
        record.FailureCount++;
        record.LastError = error;
        record.IsHealthy = false;
    }

    /// <summary>
    /// 获取设备健康状态
    /// </summary>
    public DeviceHealthRecord? GetHealth(string deviceId)
    {
        _healthRecords.TryGetValue(deviceId, out var record);
        return record;
    }

    /// <summary>
    /// 获取所有不健康的设备
    /// </summary>
    public IEnumerable<DeviceHealthRecord> GetUnhealthyDevices()
    {
        return _healthRecords.Values.Where(r => !r.IsHealthy).ToList();
    }

    /// <summary>
    /// 获取所有健康记录
    /// </summary>
    public IEnumerable<DeviceHealthRecord> GetAllHealthRecords()
    {
        return _healthRecords.Values.ToList();
    }

    /// <summary>
    /// 设备是否健康
    /// </summary>
    public bool IsDeviceHealthy(string deviceId)
    {
        return _healthRecords.TryGetValue(deviceId, out var record) && record.IsHealthy;
    }

    private void CheckHealth(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var device in _registry.GetAllDevices())
        {
            var record = _healthRecords.GetOrAdd(device.DeviceId, _ => new DeviceHealthRecord
            {
                DeviceId = device.DeviceId,
                LastHeartbeat = now
            });

            // 检查心跳超时
            if ((now - record.LastHeartbeat) > _timeoutThreshold)
            {
                record.IsHealthy = false;
                record.LastError = $"Heartbeat timeout ({_timeoutThreshold.TotalSeconds}s)";
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _heartbeatTimer?.Dispose();
        }
    }
}
