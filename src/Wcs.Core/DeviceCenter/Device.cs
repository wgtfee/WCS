namespace Wcs.Core.DeviceCenter;

using System.Collections.Generic;

/// <summary>
/// 设备类型枚举
/// </summary>
public enum DeviceTypeEnum
{
    /// <summary>
    /// 输送线
    /// </summary>
    Conveyor = 0,

    /// <summary>
    /// 机器人
    /// </summary>
    Robot = 1,

    /// <summary>
    /// 提升机
    /// </summary>
    Lift = 2,

    /// <summary>
    /// 堆垛机
    /// </summary>
    Stack = 3,

    /// <summary>
    /// 分拣机
    /// </summary>
    Sorter = 4
}

/// <summary>
/// 设备状态枚举
/// </summary>
public enum DeviceStatusEnum
{
    /// <summary>
    /// 空闲
    /// </summary>
    Idle = 0,

    /// <summary>
    /// 运行中
    /// </summary>
    Running = 1,

    /// <summary>
    /// 忙碌（执行任务）
    /// </summary>
    Busy = 2,

    /// <summary>
    /// 错误
    /// </summary>
    Error = 3,

    /// <summary>
    /// 维护
    /// </summary>
    Maintenance = 4,

    /// <summary>
    /// 暂停
    /// </summary>
    Paused = 5
}

/// <summary>
/// 设备接口
/// </summary>
public interface IDevice
{
    /// <summary>
    /// 设备 ID
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// 设备名称
    /// </summary>
    string DeviceName { get; }

    /// <summary>
    /// 设备类型
    /// </summary>
    DeviceTypeEnum DeviceType { get; }

    /// <summary>
    /// 当前状态
    /// </summary>
    DeviceStatusEnum Status { get; }

    /// <summary>
    /// 最后状态变化时间
    /// </summary>
    DateTime LastStatusChangeTime { get; }

    /// <summary>
    /// 启动设备
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止设备
    /// </summary>
    Task<bool> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 复位设备
    /// </summary>
    Task<bool> ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 暂停设备
    /// </summary>
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复设备
    /// </summary>
    Task<bool> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取设备参数
    /// </summary>
    Dictionary<string, object> GetParameters();

    /// <summary>
    /// 设置设备参数
    /// </summary>
    bool SetParameter(string key, object value);

    /// <summary>
    /// 获取设备描述
    /// </summary>
    string GetDescription();
}

/// <summary>
/// 设备事件处理器接口
/// </summary>
public interface IDeviceEventHandler
{
    /// <summary>
    /// 处理设备启动事件
    /// </summary>
    Task OnDeviceStartedAsync(IDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理设备停止事件
    /// </summary>
    Task OnDeviceStoppedAsync(IDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理设备状态变化事件
    /// </summary>
    Task OnDeviceStatusChangedAsync(IDevice device, DeviceStatusEnum oldStatus, DeviceStatusEnum newStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// 处理设备错误事件
    /// </summary>
    Task OnDeviceErrorAsync(IDevice device, string errorMessage, CancellationToken cancellationToken = default);
}

/// <summary>
/// 设备抽象基类
/// </summary>
public abstract class Device : IDevice
{
    protected DeviceStatusEnum _status = DeviceStatusEnum.Idle;
    protected DateTime _lastStatusChangeTime = DateTime.UtcNow;
    protected readonly Dictionary<string, object> _parameters = new();
    protected readonly object _statusLock = new();

    public string DeviceId { get; protected set; }

    public string DeviceName { get; protected set; }

    public abstract DeviceTypeEnum DeviceType { get; }

    public DeviceStatusEnum Status => _status;

    public DateTime LastStatusChangeTime => _lastStatusChangeTime;

    protected Device(string deviceId, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(deviceName);

        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    /// <summary>
    /// 启动设备
    /// </summary>
    public virtual async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (_status == DeviceStatusEnum.Running)
                return true;

            if (_status != DeviceStatusEnum.Idle && _status != DeviceStatusEnum.Paused)
                return false;

            var oldStatus = _status;
            _status = DeviceStatusEnum.Running;
            _lastStatusChangeTime = DateTime.UtcNow;

            // 触发状态变化回调
            _ = OnStatusChangedAsync(oldStatus, DeviceStatusEnum.Running, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// 停止设备
    /// </summary>
    public virtual async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (_status == DeviceStatusEnum.Idle)
                return true;

            var oldStatus = _status;
            _status = DeviceStatusEnum.Idle;
            _lastStatusChangeTime = DateTime.UtcNow;

            _ = OnStatusChangedAsync(oldStatus, DeviceStatusEnum.Idle, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// 复位设备
    /// </summary>
    public virtual async Task<bool> ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (_status == DeviceStatusEnum.Error || _status == DeviceStatusEnum.Maintenance)
            {
                var oldStatus = _status;
                _status = DeviceStatusEnum.Idle;
                _lastStatusChangeTime = DateTime.UtcNow;

                _ = OnStatusChangedAsync(oldStatus, DeviceStatusEnum.Idle, cancellationToken);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 暂停设备
    /// </summary>
    public virtual async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (_status != DeviceStatusEnum.Running && _status != DeviceStatusEnum.Busy)
                return false;

            var oldStatus = _status;
            _status = DeviceStatusEnum.Paused;
            _lastStatusChangeTime = DateTime.UtcNow;

            _ = OnStatusChangedAsync(oldStatus, DeviceStatusEnum.Paused, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// 恢复设备
    /// </summary>
    public virtual async Task<bool> ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (_status != DeviceStatusEnum.Paused)
                return false;

            var oldStatus = _status;
            _status = DeviceStatusEnum.Running;
            _lastStatusChangeTime = DateTime.UtcNow;

            _ = OnStatusChangedAsync(oldStatus, DeviceStatusEnum.Running, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// 获取设备参数
    /// </summary>
    public virtual Dictionary<string, object> GetParameters()
    {
        lock (_parameters)
        {
            return new Dictionary<string, object>(_parameters);
        }
    }

    /// <summary>
    /// 设置设备参数
    /// </summary>
    public virtual bool SetParameter(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_parameters)
        {
            _parameters[key] = value;
        }

        return true;
    }

    /// <summary>
    /// 获取设备描述
    /// </summary>
    public virtual string GetDescription()
    {
        return $"{DeviceType}({DeviceName}) - {DeviceId}";
    }

    /// <summary>
    /// 设置错误状态
    /// </summary>
    protected virtual void SetError(string errorMessage)
    {
        lock (_statusLock)
        {
            var oldStatus = _status;
            _status = DeviceStatusEnum.Error;
            _lastStatusChangeTime = DateTime.UtcNow;
            SetParameter("LastError", errorMessage);

            _ = OnErrorAsync(errorMessage);
        }
    }

    /// <summary>
    /// 状态变化回调（由子类处理）
    /// </summary>
    protected virtual Task OnStatusChangedAsync(DeviceStatusEnum oldStatus, DeviceStatusEnum newStatus, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 错误回调（由子类处理）
    /// </summary>
    protected virtual Task OnErrorAsync(string errorMessage)
    {
        return Task.CompletedTask;
    }
}
