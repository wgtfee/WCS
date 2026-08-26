namespace Wcs.Core.EventBus.Publisher;

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Persistence;

/// <summary>
/// 内存事件总线实现 — 可选 IEventStore 持久化集成
/// IEventStore 自身负责缓冲和批量刷盘；发布端只等待事件进入内存缓冲。
///
/// 并发设计：
/// - 订阅表为"写时复制"的不可变数组，发布路径无锁读取（ConcurrentDictionary.TryGetValue）；
/// - 非泛型 PublishAsync(IEvent) 通过表达式树按事件类型缓存分发委托，
///   首次之后零反射调用（替代每次 MakeGenericMethod + Invoke）。
/// </summary>
public class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, object[]> _subscribers = new();
    private readonly ConcurrentDictionary<Type, Delegate[]> _delegateHandlers = new();
    private readonly ConcurrentDictionary<Type, Func<IEvent, CancellationToken, Task>> _typedPublishers = new();
    private readonly object _subscribeLock = new();
    private readonly IEventStore? _eventStore;

    public EventBus(IEventStore? eventStore = null)
    {
        _eventStore = eventStore;
    }

    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            _subscribers.AddOrUpdate(eventType,
                _ => new object[] { handler },
                (_, existing) =>
                {
                    if (Array.IndexOf(existing, handler) >= 0)
                        return existing;
                    var next = new object[existing.Length + 1];
                    Array.Copy(existing, next, existing.Length);
                    next[^1] = handler;
                    return next;
                });
        }
    }

    public void Subscribe<TEvent>(EventHandlerDelegate<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            _delegateHandlers.AddOrUpdate(eventType,
                _ => new Delegate[] { handler },
                (_, existing) =>
                {
                    if (Array.IndexOf(existing, handler) >= 0)
                        return existing;
                    var next = new Delegate[existing.Length + 1];
                    Array.Copy(existing, next, existing.Length);
                    next[^1] = handler;
                    return next;
                });
        }
    }

    public void Unsubscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            if (_subscribers.TryGetValue(eventType, out var existing))
            {
                var index = Array.IndexOf(existing, handler);
                if (index >= 0)
                {
                    if (existing.Length == 1)
                        _subscribers.TryRemove(eventType, out _);
                    else
                        _subscribers[eventType] = RemoveAt(existing, index);
                }
            }
        }
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        var eventType = typeof(TEvent);

        // 无锁快照读取：数组不可变，遍历期间不会被修改。
        var handlers = _subscribers.GetValueOrDefault(eventType);
        var delegateHandlers = _delegateHandlers.GetValueOrDefault(eventType);

        switch (handlers is { Length: > 0 }, delegateHandlers is { Length: > 0 })
        {
            case (true, true):
            {
                var tasks = new Task[handlers!.Length + delegateHandlers!.Length];
                for (var i = 0; i < handlers.Length; i++)
                    tasks[i] = ExecuteHandlerAsync((IEventHandler<TEvent>)handlers[i], @event, cancellationToken);
                for (var i = 0; i < delegateHandlers.Length; i++)
                    tasks[handlers.Length + i] = SafelyExecuteDelegateAsync(
                        (EventHandlerDelegate<TEvent>)(object)delegateHandlers[i], @event, cancellationToken);
                await Task.WhenAll(tasks).ConfigureAwait(false);
                break;
            }
            case (true, false):
            {
                var tasks = new Task[handlers!.Length];
                for (var i = 0; i < handlers.Length; i++)
                    tasks[i] = ExecuteHandlerAsync((IEventHandler<TEvent>)handlers[i], @event, cancellationToken);
                await Task.WhenAll(tasks).ConfigureAwait(false);
                break;
            }
            case (false, true):
            {
                var tasks = new Task[delegateHandlers!.Length];
                for (var i = 0; i < delegateHandlers.Length; i++)
                    tasks[i] = SafelyExecuteDelegateAsync(
                        (EventHandlerDelegate<TEvent>)(object)delegateHandlers[i], @event, cancellationToken);
                await Task.WhenAll(tasks).ConfigureAwait(false);
                break;
            }
        }

        // The event store owns buffering and batched disk writes. Awaiting the
        // in-memory enqueue avoids creating one Task.Run per PLC event.
        if (_eventStore != null)
        {
            try
            {
                await _eventStore.AppendAsync(@event, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Persistence failure must not stop other event handlers.
            }
        }
    }

    public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var publisher = _typedPublishers.GetOrAdd(
            @event.GetType(),
            static (eventType, bus) => BuildTypedPublisher(eventType, bus),
            this);

        return publisher(@event, cancellationToken);
    }

    private static Func<IEvent, CancellationToken, Task> BuildTypedPublisher(Type eventType, EventBus bus)
    {
        var genericMethod = typeof(EventBus)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(PublishAsync)
                     && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == 1
                     && m.GetParameters().Length == 2)
            .MakeGenericMethod(eventType);

        // (bus, IEvent e, CancellationToken ct) => bus.PublishAsync<TE>((TE)e, ct)
        var evtParameter = Expression.Parameter(typeof(IEvent), "e");
        var ctParameter = Expression.Parameter(typeof(CancellationToken), "ct");
        var body = Expression.Call(
            Expression.Constant(bus),
            genericMethod,
            Expression.Convert(evtParameter, eventType),
            ctParameter);

        return Expression.Lambda<Func<IEvent, CancellationToken, Task>>(body, evtParameter, ctParameter).Compile();
    }

    public IEnumerable<Type> GetSubscribedEvents()
    {
        lock (_subscribeLock)
        {
            return _subscribers.Keys
                .Union(_delegateHandlers.Keys)
                .ToList();
        }
    }

    public int GetHandlerCount<TEvent>() where TEvent : IEvent
    {
        var count = 0;
        count += _subscribers.GetValueOrDefault(typeof(TEvent))?.Length ?? 0;
        count += _delegateHandlers.GetValueOrDefault(typeof(TEvent))?.Length ?? 0;
        return count;
    }

    public void ClearAllSubscriptions()
    {
        lock (_subscribeLock)
        {
            _subscribers.Clear();
            _delegateHandlers.Clear();
        }
    }

    private static object[] RemoveAt(object[] source, int index)
    {
        var next = new object[source.Length - 1];
        Array.Copy(source, 0, next, 0, index);
        Array.Copy(source, index + 1, next, index, source.Length - index - 1);
        return next;
    }

    private static async Task ExecuteHandlerAsync<TEvent>(IEventHandler<TEvent> handler, TEvent @event, CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        try
        {
            await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Suppress exceptions - events should not crash other handlers
        }
    }

    private static async Task SafelyExecuteDelegateAsync<TEvent>(EventHandlerDelegate<TEvent> handler, TEvent @event, CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        try
        {
            await handler(@event, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Suppress exceptions - events should not crash other handlers
        }
    }
}
