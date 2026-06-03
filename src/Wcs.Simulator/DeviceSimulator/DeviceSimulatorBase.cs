namespace Wcs.Simulator.DeviceSimulator;

using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 设备模拟器基类 — 所有虚拟设备继承于此
///
/// 只模拟设备行为和 PLC 反馈，不模拟 WCS Core 逻辑
/// Core（SignalBus/RuleEngine/TaskEngine）全部真实运行
/// </summary>
public abstract class DeviceSimulatorBase
{
    /// <summary>设备 ID</summary>
    public string DeviceId { get; }
    /// <summary>设备名称</summary>
    public string DeviceName { get; }
    /// <summary>信号源（用于回写模拟信号）</summary>
    protected ISignalSource SignalSource { get; }
    /// <summary>是否繁忙</summary>
    public bool IsBusy { get; protected set; }
    /// <summary>是否故障</summary>
    public bool IsFaulted { get; protected set; }

    /// <summary>模拟运输耗时（毫秒）</summary>
    public int TransportTimeMs { get; set; } = 3000;

    /// <summary>模拟信号源（可直接 Emit 信号）</summary>
    protected SimulatorSignalSource SimSignalSource => (SimulatorSignalSource)SignalSource;

    protected DeviceSimulatorBase(string deviceId, string deviceName, ISignalSource signalSource)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        SignalSource = signalSource;
    }

    /// <summary>
    /// 启动设备模拟任务
    /// </summary>
    public abstract Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止设备
    /// </summary>
    public virtual Task StopAsync(CancellationToken ct = default)
    {
        IsBusy = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 注入故障
    /// </summary>
    public virtual void InjectFault()
    {
        IsFaulted = true;
        IsBusy = false;
    }

    /// <summary>
    /// 恢复故障
    /// </summary>
    public virtual void Recover()
    {
        IsFaulted = false;
    }
}
