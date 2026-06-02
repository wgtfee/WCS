namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 任务运行时状态管理器
/// </summary>
public class TaskStateManager
{
    private readonly ConcurrentDictionary<string, TaskRuntime> _taskRuntimes = new();
    private readonly List<IStateChangeListener> _listeners = new();
    private readonly object _listenerLock = new();
    private readonly KeyedEventChannel<TaskRuntime> _channel = new();
    private readonly IEventBus? _eventBus;

    public TaskStateManager(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    public void RegisterListener(IStateChangeListener listener)
    {
        lock (_listenerLock)
        {
            if (!_listeners.Contains(listener))
                _listeners.Add(listener);
        }
    }

    public void UnregisterListener(IStateChangeListener listener)
    {
        lock (_listenerLock)
        {
            _listeners.Remove(listener);
        }
    }

    public void UpdateTaskRuntime(string taskId, TaskRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(runtime);

        var oldRuntime = _taskRuntimes.TryGetValue(taskId, out var old) ? old : null;

        if (oldRuntime != null && oldRuntime.Status == runtime.Status)
            return;

        _taskRuntimes.AddOrUpdate(taskId, runtime, (_, _) => runtime);

        if (BatchScope.IsInBatch)
        {
            BatchScope.Current!.AddChange(new StateChangeRecord(
                oldRuntime == null ? StateChangeType.Added : StateChangeType.Updated,
                taskId, oldRuntime, runtime));
        }
        else
        {
            NotifyTaskStateChanged(taskId, oldRuntime, runtime);
            _channel.Publish(taskId, runtime);
            _eventBus?.PublishAsync(new TaskStateChangedEvent
            {
                TaskId = taskId,
                OldStatus = oldRuntime?.Status ?? TaskStatusEnum.Created,
                NewStatus = runtime.Status,
                TaskRuntime = runtime
            });
        }
    }

    public TaskRuntime? GetTaskRuntime(string taskId)
    {
        _taskRuntimes.TryGetValue(taskId, out var runtime);
        return runtime;
    }

    public IEnumerable<TaskRuntime> GetAllActiveTasks()
    {
        return _taskRuntimes.Values
            .Where(t => t.Status != TaskStatusEnum.Completed && t.Status != TaskStatusEnum.Failed)
            .ToList();
    }

    public IDisposable Watch(string taskId, Action<TaskRuntime> handler)
        => _channel.Subscribe(taskId, handler);

    public Dictionary<string, TaskRuntime> GetSnapshot()
        => new(_taskRuntimes);

    public void RestoreFromSnapshot(Dictionary<string, TaskRuntime> snapshot)
    {
        _taskRuntimes.Clear();
        foreach (var kvp in snapshot)
            _taskRuntimes.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear() => _taskRuntimes.Clear();

    public int Count => _taskRuntimes.Count;

    private void NotifyTaskStateChanged(string taskId, TaskRuntime? oldRuntime, TaskRuntime newRuntime)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try { listener.OnTaskStateChanged(taskId, oldRuntime!, newRuntime); }
            catch { }
        }
    }
}
