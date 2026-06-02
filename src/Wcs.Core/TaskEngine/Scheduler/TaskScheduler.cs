namespace Wcs.Core.TaskEngine.Scheduler;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 任务调度器 - 管理任务队列、优先级排序、限流
/// </summary>
public interface ITaskScheduler
{
    /// <summary>
    /// 将任务加入队列
    /// </summary>
    Task EnqueueAsync(TaskContext task, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从队列获取下一个可执行的任务
    /// </summary>
    Task<TaskContext?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取队列中的任务数
    /// </summary>
    int GetQueueCount();

    /// <summary>
    /// 获取指定设备正在执行的任务数
    /// </summary>
    int GetDeviceTaskCount(string deviceId);

    /// <summary>
    /// 设置设备的并发限制
    /// </summary>
    void SetDeviceConcurrencyLimit(string deviceId, int limit);

    /// <summary>
    /// 将任务从队列移除
    /// </summary>
    bool Remove(string taskId);

    /// <summary>
    /// 清空队列
    /// </summary>
    void Clear();

    /// <summary>
    /// 获取队列中的所有任务
    /// </summary>
    IEnumerable<TaskContext> GetAllTasks();

    /// <summary>
    /// 释放设备并发槽位
    /// </summary>
    void ReleaseDeviceSlot(string deviceId);
}

/// <summary>
/// 基于优先级队列的任务调度器实现
/// </summary>
public class TaskScheduler : ITaskScheduler
{
    private readonly PriorityQueue<TaskContext, int> _queue = new();
    private readonly ConcurrentDictionary<string, int> _deviceTaskCount = new();
    private readonly ConcurrentDictionary<string, int> _deviceConcurrencyLimit = new();
    private readonly ConcurrentDictionary<string, TaskContext> _taskCache = new();
    private readonly object _queueLock = new();

    /// <summary>
    /// 默认并发限制
    /// </summary>
    private const int DefaultConcurrencyLimit = 3;

    /// <summary>
    /// 计算双维度排序权重：Category 为第一维度，Priority 为第二维度
    /// Recovery 类任务优先于 Production 任务
    /// </summary>
    private static int ComputeSortWeight(TaskContext task)
    {
        // Category 权重：Recovery=10000, Manual=5000, Production=0
        var categoryWeight = task.Category switch
        {
            TaskCategory.Recovery => 10000,
            TaskCategory.Manual => 5000,
            _ => 0
        };
        // Priority 权重：Emergency=4, High=3, Normal=2, Low=1
        var priorityWeight = task.PriorityLevel switch
        {
            TaskPriority.Emergency => 4,
            TaskPriority.High => 3,
            TaskPriority.Low => 1,
            _ => 2
        };
        // 兼容旧 int Priority
        var legacyPriority = task.Priority;
        return categoryWeight + priorityWeight + legacyPriority;
    }

    public async Task EnqueueAsync(TaskContext task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 缓存任务
        _taskCache.TryAdd(task.TaskId, task);

        // 检查设备并发限制
        var deviceId = task.DeviceId;
        var limit = _deviceConcurrencyLimit.GetOrAdd(deviceId, _ => DefaultConcurrencyLimit);
        var currentCount = _deviceTaskCount.GetOrAdd(deviceId, _ => 0);

        lock (_queueLock)
        {
            // 优先级越高，负值越大，越先被弹出
            var weight = ComputeSortWeight(task);
            var priority = -weight;
            _queue.Enqueue(task, priority);
        }

        await Task.CompletedTask;
    }

    public async Task<TaskContext?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        lock (_queueLock)
        {
            while (_queue.Count > 0)
            {
                if (_queue.TryDequeue(out var task, out _))
                {
                    // 检查设备并发限制
                    var deviceId = task.DeviceId;
                    var limit = _deviceConcurrencyLimit.GetOrAdd(deviceId, _ => DefaultConcurrencyLimit);
                    var currentCount = _deviceTaskCount.GetOrAdd(deviceId, _ => 0);

                    if (currentCount < limit)
                    {
                        // 增加设备任务计数
                        _deviceTaskCount.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
                        task.Status = TaskStatusEnum.Running;
                        return task;
                    }
                    else
                    {
                        // 设备达到并发限制，任务回到队列（使用新的排序权重）
                        var weight = ComputeSortWeight(task);
                        _queue.Enqueue(task, -weight);
                        return null;
                    }
                }
            }
        }

        await Task.CompletedTask;
        return null;
    }

    public int GetQueueCount()
    {
        lock (_queueLock)
        {
            return _queue.Count;
        }
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
            {
                _queue.TryDequeue(out _, out _);
            }
        }
        _deviceTaskCount.Clear();
        _taskCache.Clear();
    }

    public IEnumerable<TaskContext> GetAllTasks()
    {
        return _taskCache.Values.ToList();
    }

    /// <summary>
    /// 任务完成时，释放设备的并发计数
    /// </summary>
    public void ReleaseDeviceSlot(string deviceId)
    {
        _deviceTaskCount.AddOrUpdate(deviceId, 0, (_, count) => Math.Max(0, count - 1));
    }
}
