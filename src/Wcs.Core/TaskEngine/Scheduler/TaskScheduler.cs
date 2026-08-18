namespace Wcs.Core.TaskEngine.Scheduler;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.QueueStore;

/// <summary>
/// 任务调度器 - 管理任务队列、优先级排序、限流
/// V8: 集成 ITaskQueueStore 持久化队列，支持崩溃恢复
/// </summary>
public interface ITaskScheduler
{
    Task EnqueueAsync(TaskContext task, CancellationToken cancellationToken = default);
    Task<TaskContext?> DequeueAsync(CancellationToken cancellationToken = default);
    int GetQueueCount();
    int GetDeviceTaskCount(string deviceId);
    void SetDeviceConcurrencyLimit(string deviceId, int limit);
    bool Remove(string taskId);
    void Clear();
    IEnumerable<TaskContext> GetAllTasks();
    void ReleaseDeviceSlot(string deviceId);
    /// <summary>V8: 崩溃恢复时重新灌入待处理任务</summary>
    Task RecoverPendingTasksAsync(IEnumerable<TaskContext> pendingTasks, CancellationToken ct = default);
}

/// <summary>
/// 基于优先级队列的任务调度器实现
/// V8: 集成持久化队列存储，支持崩溃恢复
/// </summary>
public class TaskScheduler : ITaskScheduler
{
    private readonly PriorityQueue<TaskContext, int> _queue = new();
    private readonly ConcurrentDictionary<string, int> _deviceTaskCount = new();
    private readonly ConcurrentDictionary<string, int> _deviceConcurrencyLimit = new();
    private readonly ConcurrentDictionary<string, TaskContext> _taskCache = new();
    private readonly ITaskQueueStore? _queueStore;
    private readonly object _queueLock = new();

    // 一个物理设备默认只能执行一个命令流。确需并行的虚拟设备可显式调高。
    private const int DefaultConcurrencyLimit = 1;

    public TaskScheduler(ITaskQueueStore? queueStore = null)
    {
        _queueStore = queueStore;
    }

    private static int ComputeSortWeight(TaskContext task)
    {
        var categoryWeight = task.Category switch
        {
            TaskCategory.Recovery => 10000,
            TaskCategory.Manual => 5000,
            _ => 0
        };
        var priorityWeight = task.PriorityLevel switch
        {
            TaskPriority.Emergency => 4,
            TaskPriority.High => 3,
            TaskPriority.Low => 1,
            _ => 2
        };
        return categoryWeight + priorityWeight + task.Priority;
    }

    public async Task EnqueueAsync(TaskContext task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        _taskCache.TryAdd(task.TaskId, task);

        var deviceId = task.DeviceId;
        _deviceConcurrencyLimit.GetOrAdd(deviceId, _ => DefaultConcurrencyLimit);

        lock (_queueLock)
        {
            var weight = ComputeSortWeight(task);
            _queue.Enqueue(task, -weight);
        }

        // V8: 持久化到队列存储
        if (_queueStore != null)
            await _queueStore.EnqueueAsync(task, cancellationToken);
    }

    public async Task<TaskContext?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        TaskContext? dequeued = null;

        lock (_queueLock)
        {
            // 不能因为最高优先级任务的设备正忙就直接返回，否则其他空闲设备
            // 会被队头任务阻塞。最多扫描当前队列一轮，并原样放回被跳过项。
            var itemsToInspect = _queue.Count;
            var blocked = new List<(TaskContext Task, int Priority)>(itemsToInspect);

            while (itemsToInspect-- > 0 && _queue.TryDequeue(out var task, out var priority))
            {
                var deviceId = task.DeviceId;
                var limit = _deviceConcurrencyLimit.GetOrAdd(deviceId, _ => DefaultConcurrencyLimit);
                var currentCount = _deviceTaskCount.GetOrAdd(deviceId, _ => 0);

                if (currentCount < limit)
                {
                    _deviceTaskCount.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
                    task.Status = TaskStatusEnum.Running;
                    dequeued = task;
                    break;
                }

                blocked.Add((task, priority));
            }

            foreach (var item in blocked)
                _queue.Enqueue(item.Task, item.Priority);
        }

        // V8: 从持久化队列移除
        if (dequeued != null && _queueStore != null)
            await _queueStore.RemoveAsync(dequeued.TaskId, cancellationToken);

        return dequeued;
    }

    /// <summary>
    /// V8: 崩溃恢复时，将待处理任务重新灌入调度队列
    /// </summary>
    public async Task RecoverPendingTasksAsync(IEnumerable<TaskContext> pendingTasks, CancellationToken ct = default)
    {
        foreach (var task in pendingTasks)
        {
            if (ct.IsCancellationRequested) break;

            task.Status = TaskStatusEnum.Created;
            _taskCache.TryAdd(task.TaskId, task);
            _deviceConcurrencyLimit.GetOrAdd(task.DeviceId, _ => DefaultConcurrencyLimit);

            lock (_queueLock)
            {
                var weight = ComputeSortWeight(task);
                _queue.Enqueue(task, -weight);
            }
        }

        await Task.CompletedTask;
    }

    public int GetQueueCount()
    {
        lock (_queueLock) { return _queue.Count; }
    }

    public int GetDeviceTaskCount(string deviceId)
    {
        _deviceTaskCount.TryGetValue(deviceId, out var count);
        return count;
    }

    public void SetDeviceConcurrencyLimit(string deviceId, int limit)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (limit <= 0)
            throw new ArgumentException("并发限制必须大于 0", nameof(limit));
        _deviceConcurrencyLimit.AddOrUpdate(deviceId, limit, (_, _) => limit);
    }

    public bool Remove(string taskId)
    {
        return _taskCache.TryRemove(taskId, out _);
    }

    public void Clear()
    {
        lock (_queueLock)
        {
            while (_queue.Count > 0)
                _queue.TryDequeue(out _, out _);
        }
        _deviceTaskCount.Clear();
        _taskCache.Clear();
    }

    public IEnumerable<TaskContext> GetAllTasks()
    {
        return _taskCache.Values.ToList();
    }

    public void ReleaseDeviceSlot(string deviceId)
    {
        _deviceTaskCount.AddOrUpdate(deviceId, 0, (_, count) => Math.Max(0, count - 1));
    }
}
