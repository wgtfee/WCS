namespace Wcs.Core.DeviceCenter.EventHandlers;

using Wcs.Core.DeviceCenter;

/// <summary>
/// 设备状态更新处理器 - 与 StateCenter 同步
/// </summary>
public class DeviceStateUpdateHandler : IDeviceEventHandler
{
    public async Task OnDeviceStartedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        // TODO: 集成 StateCenter 更新
        // _stateCenter.UpdateDeviceState(...)
        await Task.CompletedTask;
    }

    public async Task OnDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        // TODO: 集成 StateCenter 更新
        await Task.CompletedTask;
    }

    public async Task OnDeviceStatusChangedAsync(
        IDevice device,
        DeviceStatusEnum oldStatus,
        DeviceStatusEnum newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        // TODO: 集成 StateCenter 更新
        // TODO: 发布 EventBus 事件
        await Task.CompletedTask;
    }

    public async Task OnDeviceErrorAsync(
        IDevice device,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(errorMessage);
        // TODO: 标记为错误状态
        // TODO: 发布报警事件
        await Task.CompletedTask;
    }
}

/// <summary>
/// 设备日志记录处理器
/// </summary>
public class DeviceLoggingHandler : IDeviceEventHandler
{
    private readonly List<string> _logs = new();
    private readonly object _lockObj = new();
    private const int MaxLogs = 1000;

    public async Task OnDeviceStartedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        LogEvent($"设备启动: {device.DeviceId} ({device.DeviceName})");
        await Task.CompletedTask;
    }

    public async Task OnDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        LogEvent($"设备停止: {device.DeviceId} ({device.DeviceName})");
        await Task.CompletedTask;
    }

    public async Task OnDeviceStatusChangedAsync(
        IDevice device,
        DeviceStatusEnum oldStatus,
        DeviceStatusEnum newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        LogEvent($"状态变化: {device.DeviceId} {oldStatus} -> {newStatus}");
        await Task.CompletedTask;
    }

    public async Task OnDeviceErrorAsync(
        IDevice device,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(errorMessage);
        LogEvent($"设备错误: {device.DeviceId} - {errorMessage}");
        await Task.CompletedTask;
    }

    private void LogEvent(string message)
    {
        lock (_lockObj)
        {
            var logEntry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            _logs.Add(logEntry);

            // 维护日志大小
            if (_logs.Count > MaxLogs)
            {
                _logs.RemoveRange(0, _logs.Count - MaxLogs);
            }
        }
    }

    /// <summary>
    /// 获取所有日志
    /// </summary>
    public IEnumerable<string> GetLogs()
    {
        lock (_lockObj)
        {
            return _logs.ToList();
        }
    }

    /// <summary>
    /// 获取最近的 N 条日志
    /// </summary>
    public IEnumerable<string> GetRecentLogs(int count)
    {
        lock (_lockObj)
        {
            return _logs.Skip(Math.Max(0, _logs.Count - count)).ToList();
        }
    }

    /// <summary>
    /// 清空日志
    /// </summary>
    public void ClearLogs()
    {
        lock (_lockObj)
        {
            _logs.Clear();
        }
    }
}

/// <summary>
/// 设备统计处理器
/// </summary>
public class DeviceStatisticsHandler : IDeviceEventHandler
{
    private int _totalStarted;
    private int _totalStopped;
    private int _totalStatusChanges;
    private int _totalErrors;
    private readonly object _lockObj = new();

    public async Task OnDeviceStartedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lockObj)
        {
            _totalStarted++;
        }
        await Task.CompletedTask;
    }

    public async Task OnDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lockObj)
        {
            _totalStopped++;
        }
        await Task.CompletedTask;
    }

    public async Task OnDeviceStatusChangedAsync(
        IDevice device,
        DeviceStatusEnum oldStatus,
        DeviceStatusEnum newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lockObj)
        {
            _totalStatusChanges++;
        }
        await Task.CompletedTask;
    }

    public async Task OnDeviceErrorAsync(
        IDevice device,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lockObj)
        {
            _totalErrors++;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 获取统计数据
    /// </summary>
    public Dictionary<string, int> GetStatistics()
    {
        lock (_lockObj)
        {
            return new Dictionary<string, int>
            {
                { "TotalStarted", _totalStarted },
                { "TotalStopped", _totalStopped },
                { "TotalStatusChanges", _totalStatusChanges },
                { "TotalErrors", _totalErrors }
            };
        }
    }

    /// <summary>
    /// 重置统计
    /// </summary>
    public void ResetStatistics()
    {
        lock (_lockObj)
        {
            _totalStarted = 0;
            _totalStopped = 0;
            _totalStatusChanges = 0;
            _totalErrors = 0;
        }
    }
}
