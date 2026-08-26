namespace Wcs.Core.TaskEngine.Orchestrator;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 任务编排器接口
/// </summary>
public interface ITaskOrchestrator
{
    /// <summary>
    /// 启动任务
    /// </summary>
    Task<bool> StartTaskAsync(TaskContext task, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取任务状态
    /// </summary>
    TaskStatusEnum? GetTaskStatus(string taskId);

    /// <summary>
    /// 完成任务
    /// </summary>
    Task CompleteTaskAsync(string taskId, bool success, string? errorMessage = null, object? result = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消任务
    /// </summary>
    Task<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 恢复任务
    /// </summary>
    Task<bool> RecoverTaskAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 等待任务完成
    /// </summary>
    Task<TaskContext?> WaitTaskAsync(string taskId, int timeoutMs = 30000, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有活跃任务
    /// </summary>
    IEnumerable<TaskContext> GetActiveTasks();

    /// <summary>
    /// 获取指定任务的信息
    /// </summary>
    TaskContext? GetTaskInfo(string taskId);
}

/// <summary>
/// 任务编排器实现
/// </summary>
public class TaskOrchestrator : ITaskOrchestrator
{
    /// <summary>终态任务归档上限：只保留最近完成的任务供查询，防止 _tasks 无界增长。</summary>
    private const int MaxArchivedTasks = 1000;

    private readonly IStateCenter _stateCenter;
    private readonly ITaskScheduler _scheduler;
    private readonly ConcurrentDictionary<string, TaskContext> _tasks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskContext>> _completionSources = new();
    private readonly ConcurrentQueue<TaskContext> _archivedTasks = new();

    public TaskOrchestrator(IStateCenter stateCenter, ITaskScheduler scheduler)
    {
        _stateCenter = stateCenter ?? throw new ArgumentNullException(nameof(stateCenter));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public async Task<bool> StartTaskAsync(TaskContext task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 更新状态为 Running
        task.Status = TaskStatusEnum.Running;
        task.StartTime = DateTime.UtcNow;

        // 保存到 StateCenter
        _stateCenter.UpdateTaskRuntime(task.TaskId, new TaskRuntime
        {
            TaskId = task.TaskId,
            Status = TaskStatusEnum.Running,
            Priority = task.Priority,
            RouteId = task.RouteId,
            StartTime = task.StartTime,
            Parameters = task.Parameters
        });

        // 缓存任务
        _tasks.TryAdd(task.TaskId, task);

        // 创建完成源
        var completionSource = new TaskCompletionSource<TaskContext>();
        _completionSources.TryAdd(task.TaskId, completionSource);

        await Task.CompletedTask;
        return true;
    }

    public TaskStatusEnum? GetTaskStatus(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _stateCenter.GetTaskRuntime(taskId)?.Status;
    }

    public async Task CompleteTaskAsync(string taskId, bool success, string? errorMessage = null, object? result = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (!_tasks.TryGetValue(taskId, out var task))
            return;

        // 更新任务
        task.EndTime = DateTime.UtcNow;
        task.Result = result;
        task.ErrorMessage = errorMessage;
        task.Status = success ? TaskStatusEnum.Completed : TaskStatusEnum.Failed;

        // 更新 StateCenter
        _stateCenter.UpdateTaskRuntime(taskId, new TaskRuntime
        {
            TaskId = taskId,
            Status = task.Status,
            Priority = task.Priority,
            RouteId = task.RouteId,
            StartTime = task.StartTime,
            EndTime = task.EndTime,
            Parameters = task.Parameters
        });

        // 释放设备并发计数
        if (!string.IsNullOrEmpty(task.DeviceId))
        {
            _scheduler.ReleaseDeviceSlot(task.DeviceId);
        }

        // 触发完成事件
        if (_completionSources.TryRemove(taskId, out var completionSource))
        {
            completionSource.SetResult(task);
        }

        ArchiveTerminal(task);

        await Task.CompletedTask;
    }

    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        // 更新状态
        task.Status = TaskStatusEnum.Cancelled;
        task.EndTime = DateTime.UtcNow;

        // 更新 StateCenter
        _stateCenter.UpdateTaskRuntime(taskId, new TaskRuntime
        {
            TaskId = taskId,
            Status = TaskStatusEnum.Cancelled,
            Priority = task.Priority,
            RouteId = task.RouteId,
            EndTime = task.EndTime,
            Parameters = task.Parameters
        });

        // 释放设备并发计数
        if (!string.IsNullOrEmpty(task.DeviceId))
        {
            _scheduler.ReleaseDeviceSlot(task.DeviceId);
        }

        // 触发完成事件
        if (_completionSources.TryRemove(taskId, out var completionSource))
        {
            completionSource.SetCanceled();
        }

        ArchiveTerminal(task);

        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> RecoverTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        // 检查是否可重试
        if (!task.IsRetryable || task.RetryCount >= task.MaxRetries)
            return false;

        // 克隆任务用于重试
        var retryTask = task.Clone();
        retryTask.Status = TaskStatusEnum.Created;

        // 重新加入队列
        await _scheduler.EnqueueAsync(retryTask, cancellationToken);

        // 更新原任务为已恢复
        task.Status = TaskStatusEnum.Recovered;

        return true;
    }

    public async Task<TaskContext?> WaitTaskAsync(string taskId, int timeoutMs = 30000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (!_completionSources.TryGetValue(taskId, out var completionSource))
            return null;

        try
        {
            var result = await completionSource.Task.ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public IEnumerable<TaskContext> GetActiveTasks()
    {
        return _tasks.Values
            .Where(t => t.Status != TaskStatusEnum.Completed && t.Status != TaskStatusEnum.Failed && t.Status != TaskStatusEnum.Cancelled)
            .ToList();
    }

    public TaskContext? GetTaskInfo(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
            return task;

        // 终态任务从 _tasks 移除后，回退到归档队列中查找（诊断路径，量级有限）。
        // 注意：ConcurrentQueue 无随机访问，此处线性扫描可接受。
        return _archivedTasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));
    }

    /// <summary>终态任务移出活跃表并进入有界归档队列。</summary>
    private void ArchiveTerminal(TaskContext task)
    {
        _tasks.TryRemove(task.TaskId, out _);
        _archivedTasks.Enqueue(task);

        while (_archivedTasks.Count > MaxArchivedTasks)
        {
            _archivedTasks.TryDequeue(out _);
        }
    }
}
