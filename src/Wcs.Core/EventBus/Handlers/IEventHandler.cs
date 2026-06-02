namespace Wcs.Core.EventBus.Handlers;

using Wcs.Core.EventBus.Events;

/// <summary>
/// 事件处理器泛型接口
/// </summary>
/// <typeparam name="TEvent">事件类型</typeparam>
public interface IEventHandler<in TEvent> where TEvent : IEvent
{
    /// <summary>
    /// 处理事件
    /// </summary>
    /// <param name="event">事件实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// 事件处理器非泛型接口
/// </summary>
public interface IEventHandler
{
    /// <summary>
    /// 获取支持的事件类型
    /// </summary>
    Type EventType { get; }

    /// <summary>
    /// 处理事件（动态调用）
    /// </summary>
    Task HandleAsync(IEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>
/// 事件处理器执行委托
/// </summary>
/// <typeparam name="TEvent">事件类型</typeparam>
/// <param name="event">事件实例</param>
/// <param name="cancellationToken">取消令牌</param>
public delegate Task EventHandlerDelegate<in TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : IEvent;

/// <summary>
/// 事件处理器元数据
/// </summary>
public class EventHandlerMetadata
{
    public string HandlerId { get; set; } = Guid.NewGuid().ToString("N");

    public Type EventType { get; set; } = typeof(IEvent);

    public Type HandlerType { get; set; } = typeof(IEventHandler);

    public int Order { get; set; }

    public bool IsAsync { get; set; } = true;
}
