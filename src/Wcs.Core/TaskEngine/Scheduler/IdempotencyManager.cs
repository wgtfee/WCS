namespace Wcs.Core.TaskEngine.Scheduler;

using System.Collections.Concurrent;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 幂等性管理器接口
/// </summary>
public interface IIdempotencyManager
{
    /// <summary>
    /// 检查任务是否已处理过
    /// </summary>
    bool IsTaskProcessed(string taskId);

    /// <summary>
    /// 获取任务的处理结果
    /// </summary>
    TaskIdempotencyResult? GetTaskResult(string taskId);

    /// <summary>
    /// 记录任务处理结果
    /// </summary>
    void RecordTaskResult(string taskId, TaskIdempotencyResult result);

    /// <summary>
    /// 清除指定任务的处理记录
    /// </summary>
    bool RemoveTaskRecord(string taskId);

    /// <summary>
    /// 清除所有处理记录
    /// </summary>
    void ClearAll();

    /// <summary>
    /// 获取处理过的任务数
    /// </summary>
    int GetProcessedTaskCount();
}

/// <summary>
/// 任务幂等性结果
/// </summary>
public class TaskIdempotencyResult
{
    public string TaskId { get; set; } = string.Empty;

    public DateTime ProcessedTime { get; set; }

    public bool Success { get; set; }

    public object? Result { get; set; }

    public string? ErrorMessage { get; set; }

    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// 幂等性管理器实现
/// </summary>
public class IdempotencyManager : IIdempotencyManager
{
    private readonly ConcurrentDictionary<string, TaskIdempotencyResult> _processedTasks = new();

    /// <summary>
    /// 最大缓存数量
    /// </summary>
    private const int MaxCacheSize = 10000;

    public bool IsTaskProcessed(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _processedTasks.ContainsKey(taskId);
    }

    public TaskIdempotencyResult? GetTaskResult(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        _processedTasks.TryGetValue(taskId, out var result);
        return result;
    }

    public void RecordTaskResult(string taskId, TaskIdempotencyResult result)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(result);

        // 如果缓存满了，清除一些旧记录
        if (_processedTasks.Count >= MaxCacheSize)
        {
            var oldestTask = _processedTasks
                .OrderBy(x => x.Value.ProcessedTime)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(oldestTask.Key))
            {
                _processedTasks.TryRemove(oldestTask.Key, out _);
            }
        }

        _processedTasks.AddOrUpdate(taskId, result, (_, _) => result);
    }

    public bool RemoveTaskRecord(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _processedTasks.TryRemove(taskId, out _);
    }

    public void ClearAll()
    {
        _processedTasks.Clear();
    }

    public int GetProcessedTaskCount()
    {
        return _processedTasks.Count;
    }
}
