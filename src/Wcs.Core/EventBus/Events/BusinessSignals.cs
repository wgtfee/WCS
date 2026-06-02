namespace Wcs.Core.EventBus.Events;

/// <summary>
/// 输送线就绪信号事件 — SignalMapper 将 PLC 位映射为此事件
/// </summary>
public class ConveyorReadyChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>是否就绪</summary>
    public bool Ready { get; set; }

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>
/// 输送线速度变化信号事件
/// </summary>
public class ConveyorSpeedChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;

    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>当前速度（mm/s）</summary>
    public int Speed { get; set; }

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>
/// 托盘到位信号事件
/// </summary>
public class PalletArrivedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    /// <summary>设备 ID（站点）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>托盘条码</summary>
    public string? Barcode { get; set; }

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>
/// 设备故障信号事件
/// </summary>
public class DeviceFaultEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;

    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>故障代码</summary>
    public string FaultCode { get; set; } = string.Empty;

    /// <summary>故障描述</summary>
    public string? Description { get; set; }

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>
/// 模式切换信号事件（自动/手动/维护）
/// </summary>
public class ModeSwitchedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;

    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>新模式：Auto/Manual/Maintenance</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>
/// 紧急停止信号事件
/// </summary>
public class EmergencyStopEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;

    /// <summary>触发区域/设备</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>来源 PLC 名称</summary>
    public string PlcName { get; set; } = string.Empty;
}
