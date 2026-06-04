namespace Wcs.Core.EventDetection;

/// <summary>
/// 边沿类型 — PLC 信号的变化方向
/// </summary>
public enum EdgeType
{
    /// <summary>上升沿：false→true, 0→1</summary>
    Rising,
    /// <summary>下降沿：true→false, 1→0</summary>
    Falling,
    /// <summary>双边沿：任何变化都触发</summary>
    Both
}

/// <summary>
/// 事件检测规则 — 将 PLC 结构体字段的边沿变化映射为业务事件
///
/// 一个规则 = "当某个字段发生特定方向的变化时，生成某个事件"
///
/// 命名约定（无需逐条配置）：
///   字段名以 _RequestOut / _Arrived 结尾 → 上升沿 → PalletArrivedEvent
///   字段名以 _Fault 结尾               → 上升沿 → DeviceFaultEvent
///   字段名以 _Ready 结尾               → 上升沿 → ConveyorReadyEvent
///   字段名以 _Speed / _Count 结尾       → 任何变化 → 值变化事件
///
/// 逐条配置（精确控制）：
///   new EventDetectionRule {
///       SignalId = "CV01.PalletArrived",
///       DeviceId = "CV01",
///       FieldName = nameof(DB1_StatusBlock.CV01_PalletArrived),
///       Edge = EdgeType.Rising,
///       TargetEventType = "Wcs.Core.EventBus.Events.PalletArrivedEvent"
///   }
/// </summary>
public class EventDetectionRule
{
    /// <summary>规则唯一标识</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>目标设备 ID（用于事件属性填充）</summary>
    public string? DeviceId { get; set; }

    /// <summary>监控的字段名（如 "CV01_PalletArrived"）</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>边沿类型</summary>
    public EdgeType Edge { get; set; } = EdgeType.Rising;

    /// <summary>目标事件类型全名（为空则从命名约定推断）</summary>
    public string? TargetEventType { get; set; }

    /// <summary>额外属性映射</summary>
    public Dictionary<string, string>? PropertyMappings { get; set; }
}
