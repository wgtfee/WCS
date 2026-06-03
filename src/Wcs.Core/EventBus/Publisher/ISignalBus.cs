namespace Wcs.Core.EventBus.Publisher;

/// <summary>
/// 信号总线接口 — 专用于 PLC 信号事件（与业务事件总线分离）
/// 防止高频率的 PLC 信号淹没业务事件
/// </summary>
public interface ISignalBus : IEventBus
{
    /// <summary>信号总线名称标识</summary>
    string BusName => "SignalBus";
}
