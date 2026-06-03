namespace Wcs.Core.TaskEngine.QueueStore;

using System.Collections.Concurrent;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 内存任务队列存储（生产环境应替换为 Redis/DB 实现）
/// 满足 ITaskQueueStore 接口，支持崩溃后通过 RecoveryManager 恢复
/// </summary>
public class InMemoryTaskQueueStore : ITaskQueueStore
{
    private readonly ConcurrentDictionary<string, TaskContext> _pending = new();

    public Task EnqueueAsync(TaskContext task, CancellationToken ct = default)
    {
        _pending.TryAdd(task.TaskId, task);
        return Task.CompletedTask;
    }

    public Task<List<TaskContext>> GetPendingTasksAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_pending.Values.ToList());
    }

    public Task RemoveAsync(string taskId, CancellationToken ct = default)
    {
        _pending.TryRemove(taskId, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    public Task<int> GetCountAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_pending.Count);
    }
}
