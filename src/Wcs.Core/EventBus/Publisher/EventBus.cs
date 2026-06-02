namespace Wcs.Core.EventBus.Publisher;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Persistence;

/// <summary>
/// 内存事件总线实现 — 可选 IEventStore 持久化集成
/// 持久化为 fire-and-forget 模式，不影响主事件发布流程
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

        // Fire-and-forget: 持久化事件到 EventStore（不影响主流程）
        if (_eventStore != null)
        {
            PersistFireAndForget(@event);
        }
    }

    /// <summary>
    /// fire-and-forget 事件持久化
    /// </summary>
    private void PersistFireAndForget<TEvent>(TEvent @event) where TEvent : IEvent
    {
        Task.Run(async () =>
        {
            try
            {
                await _eventStore!.AppendAsync(@event);
            }
            catch
            {
                // 持久化失败不应影响事件发布
            }
        });
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
