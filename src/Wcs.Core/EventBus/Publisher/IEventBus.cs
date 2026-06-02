namespace Wcs.Core.EventBus.Publisher;

using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;

/// <summary>
/// 事件总线接口
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// 订阅事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">处理器实例</param>
    void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IEvent;

    /// <summary>
    /// 订阅事件（委托方式）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">处理器委托</param>
    void Subscribe<TEvent>(EventHandlerDelegate<TEvent> handler) where TEvent : IEvent;

    /// <summary>
    /// 取消订阅
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">处理器实例</param>
    void Unsubscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IEvent;

    /// <summary>
    /// 发布事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>处理结果</returns>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent;

    /// <summary>
    /// 发布事件（非泛型版本）
    /// </summary>
    Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有已注册的事件类型
    /// </summary>
    IEnumerable<Type> GetSubscribedEvents();

    /// <summary>
    /// 获取指定事件类型的处理器数
    /// </summary>
    int GetHandlerCount<TEvent>() where TEvent : IEvent;

    /// <summary>
    /// 清空所有订阅
    /// </summary>
    void ClearAllSubscriptions();
}

/// <summary>
/// 事件发布结果
/// </summary>
public class EventPublishResult
{
    public string EventId { get; set; } = string.Empty;

    public DateTime PublishTime { get; set; }

    public int HandlersCount { get; set; }

    public int SuccessCount { get; set; }

    public int FailedCount { get; set; }

    public List<EventHandlerException> Exceptions { get; set; } = new();

    public bool IsSuccessful => FailedCount == 0;
}

/// <summary>
/// 事件处理异常
/// </summary>
public class EventHandlerException
{
    public string HandlerId { get; set; } = string.Empty;

    public Type HandlerType { get; set; } = typeof(IEventHandler);

    public Exception Exception { get; set; } = null!;

    public DateTime OccurTime { get; set; } = DateTime.UtcNow;
}
