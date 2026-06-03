namespace Wcs.Core.EventBus.Events;

/// <summary>输送线就绪信号</summary>
public class ConveyorReadyChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public string DeviceId { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>托盘到位信号</summary>
public class PalletArrivedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public string DeviceId { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>设备故障信号</summary>
public class DeviceFaultEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;
    public string DeviceId { get; set; } = string.Empty;
    public string FaultCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>模式切换信号</summary>
public class ModeSwitchedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public string DeviceId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>急停信号</summary>
public class EmergencyStopEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
}

/// <summary>速度变化信号</summary>
public class ConveyorSpeedChangedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;
    public string DeviceId { get; set; } = string.Empty;
    public int Speed { get; set; }
    public string PlcName { get; set; } = string.Empty;
}
