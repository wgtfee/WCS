namespace Wcs.Core.DeviceCenter;

using Wcs.Core.DeviceCenter;

/// <summary>
/// 输送线设备
/// </summary>
public class ConveyorDevice : Device
{
    public override DeviceTypeEnum DeviceType => DeviceTypeEnum.Conveyor;

    /// <summary>
    /// 最大速度（0-100%）
    /// </summary>
    public int MaxSpeed { get; set; } = 100;

    /// <summary>
    /// 当前速度（0-100%）
    /// </summary>
    public int CurrentSpeed { get; set; } = 0;

    /// <summary>
    /// 长度（单位：mm）
    /// </summary>
    public int Length { get; set; } = 1000;

    public ConveyorDevice(string deviceId, string deviceName) 
        : base(deviceId, deviceName)
    {
        SetParameter("MaxSpeed", MaxSpeed);
        SetParameter("Length", Length);
    }

    /// <summary>
    /// 设置速度
    /// </summary>
    public bool SetSpeed(int speed)
    {
        if (speed < 0 || speed > MaxSpeed)
            return false;

        CurrentSpeed = speed;
        SetParameter("CurrentSpeed", speed);
        return true;
    }

    /// <summary>
    /// 获取当前速度
    /// </summary>
    public int GetSpeed()
    {
        return CurrentSpeed;
    }

    public override async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Status == DeviceStatusEnum.Running)
            return true;

        var result = await base.StartAsync(cancellationToken).ConfigureAwait(false);
        
        if (result)
        {
            // 启动时设置默认速度
            SetSpeed(80);
        }

        return result;
    }

    public override async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (result)
        {
            CurrentSpeed = 0;
            SetParameter("CurrentSpeed", 0);
        }

        return result;
    }

    public override string GetDescription()
    {
        return $"输送线({DeviceName}) - {DeviceId} [长度:{Length}mm, 最大速度:{MaxSpeed}%, 当前速度:{CurrentSpeed}%]";
    }
}

/// <summary>
/// 机器人设备
/// </summary>
public class RobotDevice : Device
{
    public override DeviceTypeEnum DeviceType => DeviceTypeEnum.Robot;

    /// <summary>
    /// 最大负载（单位：kg）
    /// </summary>
    public int MaxWorkload { get; set; } = 50;

    /// <summary>
    /// 当前负载（单位：kg）
    /// </summary>
    public int CurrentWorkload { get; set; } = 0;

    /// <summary>
    /// 运动范围（单位：mm）
    /// </summary>
    public int ReachDistance { get; set; } = 1000;

    /// <summary>
    /// 当前执行的任务 ID
    /// </summary>
    public string? CurrentTaskId { get; set; }

    /// <summary>
    /// 任务超时时间（单位：毫秒）
    /// </summary>
    public int TaskTimeout { get; set; } = 60000;

    public RobotDevice(string deviceId, string deviceName)
        : base(deviceId, deviceName)
    {
        SetParameter("MaxWorkload", MaxWorkload);
        SetParameter("ReachDistance", ReachDistance);
        SetParameter("TaskTimeout", TaskTimeout);
    }

    /// <summary>
    /// 执行任务
    /// </summary>
    public async Task<bool> ExecuteTaskAsync(string taskId, int estimatedDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (Status != DeviceStatusEnum.Idle)
            return false;

        CurrentTaskId = taskId;
        SetParameter("CurrentTaskId", taskId);

        // 状态转换为 Busy
        lock (_statusLock)
        {
            var oldStatus = _status;
            _status = DeviceStatusEnum.Busy;
            _lastStatusChangeTime = DateTime.UtcNow;
        }

        try
        {
            // 模拟任务执行（实际应与 PLC 通信）
            await Task.Delay(Math.Min(estimatedDuration, TaskTimeout), cancellationToken).ConfigureAwait(false);

            // 任务完成，返回 Idle
            lock (_statusLock)
            {
                _status = DeviceStatusEnum.Idle;
                _lastStatusChangeTime = DateTime.UtcNow;
                CurrentTaskId = null;
                SetParameter("CurrentTaskId", null);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // 任务被取消
            lock (_statusLock)
            {
                _status = DeviceStatusEnum.Idle;
                _lastStatusChangeTime = DateTime.UtcNow;
                CurrentTaskId = null;
            }

            return false;
        }
    }

    /// <summary>
    /// 设置负载
    /// </summary>
    public bool SetWorkload(int workload)
    {
        if (workload < 0 || workload > MaxWorkload)
            return false;

        CurrentWorkload = workload;
        SetParameter("CurrentWorkload", workload);
        return true;
    }

    /// <summary>
    /// 取消当前任务
    /// </summary>
    public bool CancelTask()
    {
        if (Status != DeviceStatusEnum.Busy)
            return false;

        lock (_statusLock)
        {
            _status = DeviceStatusEnum.Idle;
            _lastStatusChangeTime = DateTime.UtcNow;
            CurrentTaskId = null;
            SetParameter("CurrentTaskId", null);
        }

        return true;
    }

    public override string GetDescription()
    {
        var taskInfo = string.IsNullOrEmpty(CurrentTaskId) ? "无" : CurrentTaskId;
        return $"机器人({DeviceName}) - {DeviceId} [范围:{ReachDistance}mm, 最大负载:{MaxWorkload}kg, 当前负载:{CurrentWorkload}kg, 当前任务:{taskInfo}]";
    }
}

/// <summary>
/// 提升机设备
/// </summary>
public class LiftDevice : Device
{
    public override DeviceTypeEnum DeviceType => DeviceTypeEnum.Lift;

    /// <summary>
    /// 最大承重（单位：kg）
    /// </summary>
    public int MaxCapacity { get; set; } = 1000;

    /// <summary>
    /// 当前承重
    /// </summary>
    public int CurrentLoad { get; set; } = 0;

    /// <summary>
    /// 提升高度（单位：mm）
    /// </summary>
    public int LiftHeight { get; set; } = 5000;

    public LiftDevice(string deviceId, string deviceName)
        : base(deviceId, deviceName)
    {
        SetParameter("MaxCapacity", MaxCapacity);
        SetParameter("LiftHeight", LiftHeight);
    }

    public override string GetDescription()
    {
        return $"提升机({DeviceName}) - {DeviceId} [高度:{LiftHeight}mm, 最大承重:{MaxCapacity}kg, 当前承重:{CurrentLoad}kg]";
    }
}

/// <summary>
/// 堆垛机设备
/// </summary>
public class StackDevice : Device
{
    public override DeviceTypeEnum DeviceType => DeviceTypeEnum.Stack;

    /// <summary>
    /// 最大承重（单位：kg）
    /// </summary>
    public int MaxCapacity { get; set; } = 500;

    /// <summary>
    /// 当前承重
    /// </summary>
    public int CurrentLoad { get; set; } = 0;

    /// <summary>
    /// 堆垛层数
    /// </summary>
    public int MaxLayers { get; set; } = 10;

    public StackDevice(string deviceId, string deviceName)
        : base(deviceId, deviceName)
    {
        SetParameter("MaxCapacity", MaxCapacity);
        SetParameter("MaxLayers", MaxLayers);
    }

    public override string GetDescription()
    {
        return $"堆垛机({DeviceName}) - {DeviceId} [层数:{MaxLayers}, 最大承重:{MaxCapacity}kg, 当前承重:{CurrentLoad}kg]";
    }
}
