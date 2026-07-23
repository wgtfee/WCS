namespace Wcs.Core.EventBus.Publisher;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Persistence;

/// <summary>
/// 内存事件总线实现 — 可选 IEventStore 持久化集成
/// IEventStore 自身负责缓冲和批量刷盘；发布端只等待事件进入内存缓冲。
/// </summary>
public class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _subscribers = new();
    private readonly ConcurrentDictionary<Type, List<Delegate>> _delegateHandlers = new();
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
            _subscribers
                .AddOrUpdate(eventType, 
                    _ => new List<object> { handler },
                    (_, list) =>
                    {
                        if (!list.Contains(handler))
                        {
                            list.Add(handler);
                        }
                        return list;
                    });
        }
    }

    public void Subscribe<TEvent>(EventHandlerDelegate<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            _delegateHandlers
                .AddOrUpdate(eventType,
                    _ => new List<Delegate> { handler },
                    (_, list) =>
                    {
                        if (!list.Contains(handler))
                        {
                            list.Add(handler);
                        }
                        return list;
                    });
        }
    }

    public void Unsubscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            if (_subscribers.TryGetValue(eventType, out var handlers))
            {
                handlers.Remove(handler);
            }
        }
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        var eventType = typeof(TEvent);

        // Get handlers
        List<object>? handlers = null;
        List<Delegate>? delegateHandlers = null;

        lock (_subscribeLock)
        {
            _subscribers.TryGetValue(eventType, out handlers);
            _delegateHandlers.TryGetValue(eventType, out delegateHandlers);
        }

        var tasks = new List<Task>();

        // Execute object handlers
        if (handlers != null && handlers.Count > 0)
        {
            foreach (var handler in handlers.ToList())
            {
                var task = ExecuteHandlerAsync((IEventHandler<TEvent>)handler, @event, cancellationToken);
                tasks.Add(task);
            }
        }

        // Execute delegate handlers
        if (delegateHandlers != null && delegateHandlers.Count > 0)
        {
            foreach (var handler in delegateHandlers.ToList())
            {
                var delegateTask = SafelyExecuteDelegateAsync((EventHandlerDelegate<TEvent>)(object)handler, @event, cancellationToken);
                tasks.Add(delegateTask);
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
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

        var eventType = @event.GetType();
        var method = typeof(EventBus)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .First(m => m.Name == nameof(PublishAsync)
                     && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == 1
                     && m.GetParameters().Length == 2);

        var genericMethod = method.MakeGenericMethod(eventType);
        var task = (Task)genericMethod.Invoke(this, new object[] { @event, cancellationToken })!;
        return task;
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
        var eventType = typeof(TEvent);
        lock (_subscribeLock)
        {
            var count = 0;
            if (_subscribers.TryGetValue(eventType, out var handlers))
            {
                count += handlers.Count;
            }
            if (_delegateHandlers.TryGetValue(eventType, out var delegateHandlers))
            {
                count += delegateHandlers.Count;
            }
            return count;
        }
    }

    public void ClearAllSubscriptions()
    {
        lock (_subscribeLock)
        {
            _subscribers.Clear();
            _delegateHandlers.Clear();
        }
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
