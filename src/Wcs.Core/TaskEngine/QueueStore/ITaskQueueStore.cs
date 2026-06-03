namespace Wcs.Core.TaskEngine.QueueStore;

using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 持久化任务队列存储接口 — 使 TaskScheduler 的队列支持崩溃恢复
///
/// 用途：WCS 崩溃后重启时，RecoveryManager 调用 GetPendingTasksAsync()
/// 将未完成的任务重新灌入 Scheduler，避免队列丢失。
/// </summary>
public interface ITaskQueueStore
{
    /// <summary>
    /// 将任务写入持久化队列
    /// </summary>
    Task EnqueueAsync(TaskContext task, CancellationToken ct = default);

    /// <summary>
    /// 获取所有待处理的任务（启动恢复时使用）
    /// </summary>
    Task<List<TaskContext>> GetPendingTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// 从队列移除任务（出队或取消时调用）
    /// </summary>
    Task RemoveAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// 清空队列
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取队列中的任务数
    /// </summary>
    Task<int> GetCountAsync(CancellationToken ct = default);
}
