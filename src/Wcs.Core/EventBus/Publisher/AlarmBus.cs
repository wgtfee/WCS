namespace Wcs.Core.EventBus.Publisher;

using Wcs.Core.EventBus.Persistence;

/// <summary>
/// 报警总线 — 专用于报警事件的独立通道
///
/// 三分区架构：
/// - SignalBus：PLC 信号事件（高频，独立通道）
/// - DomainBus：业务事件（标准 EventBus）
/// - AlarmBus：报警事件（关键事件，独立通道）
///
/// 防止报警事件被高频 PLC 信号淹没，也防止报警风暴影响业务事件处理。
/// </summary>
public class AlarmBus : EventBus, IEventBus
{
    public string BusName => "AlarmBus";

    public AlarmBus(IEventStore? eventStore = null)
        : base(eventStore)
    {
    }
}
