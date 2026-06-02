namespace Wcs.Core.StateCenter.Features;

using System.Collections.Concurrent;

/// <summary>
/// 细粒度 per-key 事件通道 — 允许订阅特定状态键的变更
/// 替代全局 IStateChangeListener，只接收关注的状态变更
/// </summary>
public sealed class KeyedEventChannel<T>
{
    private readonly ConcurrentDictionary<string, List<Action<T>>> _subscribers = new();

    /// <summary>
    /// 订阅指定 key 的变更通知
    /// </summary>
    public IDisposable Subscribe(string key, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(handler);

        _subscribers.AddOrUpdate(key,
            _ => new List<Action<T>> { handler },
            (_, list) =>
            {
                list.Add(handler);
                return list;
            });

        return new Subscription(() =>
        {
            if (_subscribers.TryGetValue(key, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                    _subscribers.TryRemove(key, out _);
            }
        });
    }

    /// <summary>
    /// 发布指定 key 的变更
    /// </summary>
    public void Publish(string key, T value)
    {
        if (_subscribers.TryGetValue(key, out var handlers))
        {
            foreach (var handler in handlers.ToList())
            {
                try { handler(value); }
                catch
                {
                    // Suppress per-handler exceptions
                }
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _unsubscribe();
        }
    }
}
