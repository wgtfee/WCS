namespace Wcs.Core.EventBus.Publisher;

using Wcs.Core.EventBus.Persistence;

/// <summary>
/// 信号总线实现 — 专用于 PLC 信号事件，与业务事件总线物理隔离
/// 避免高频 PLC 信号对业务事件处理造成冲击
/// </summary>
public class SignalBus : EventBus, ISignalBus
{
    public string BusName => "SignalBus";

    public SignalBus(IEventStore? eventStore = null)
        : base(eventStore)
    {
    }
}
