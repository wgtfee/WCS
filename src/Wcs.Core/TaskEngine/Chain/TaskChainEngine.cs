namespace Wcs.Core.TaskEngine.Chain;

using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 任务链 - 一组有序的任务
/// </summary>
public class TaskChain
{
    /// <summary>
    /// 链 ID
    /// </summary>
    public string ChainId { get; set; }

    /// <summary>
    /// 链中的任务列表
    /// </summary>
    public List<TaskContext> Tasks { get; set; } = new();

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 路由 ID
    /// </summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>
    /// 链执行模式
    /// </summary>
    public ChainExecutionMode Mode { get; set; } = ChainExecutionMode.Serial;

    public TaskChain(string chainId = "")
    {
        ChainId = string.IsNullOrEmpty(chainId) ? Guid.NewGuid().ToString("N") : chainId;
    }

    /// <summary>
    /// 添加任务到链
    /// </summary>
    public TaskChain Add(TaskContext task)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.ParentTaskId = ChainId;
        Tasks.Add(task);
        return this;
    }

    /// <summary>
    /// 链式调用 - 添加下一个任务
    /// </summary>
    public TaskChain Then(TaskContext task)
    {
        return Add(task);
    }

    /// <summary>
    /// 获取链中的任务数
    /// </summary>
    public int Count => Tasks.Count;

    /// <summary>
    /// 检查链是否为空
    /// </summary>
    public bool IsEmpty => Tasks.Count == 0;
}

/// <summary>
/// 任务链执行模式
/// </summary>
public enum ChainExecutionMode
{
    /// <summary>
    /// 顺序执行
    /// </summary>
    Serial = 0,

    /// <summary>
    /// 并行执行
    /// </summary>
    Parallel = 1,

    /// <summary>
    /// 条件执行
    /// </summary>
    Conditional = 2
}

/// <summary>
/// 任务链执行引擎接口
/// </summary>
public interface ITaskChainEngine
{
    /// <summary>
    /// 执行任务链
    /// </summary>
    Task<TaskChainResult> ExecuteChainAsync(TaskChain chain, CancellationToken cancellationToken = default);

    /// <summary>
    /// 并行执行任务
    /// </summary>
    Task<TaskChainResult> ExecuteParallelAsync(IEnumerable<TaskContext> tasks, CancellationToken cancellationToken = default);

    /// <summary>
    /// 顺序执行任务
    /// </summary>
    Task<TaskChainResult> ExecuteSerialAsync(IEnumerable<TaskContext> tasks, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取链执行结果
    /// </summary>
    TaskChainResult? GetChainResult(string chainId);
}

/// <summary>
/// 任务链执行结果
/// </summary>
public class TaskChainResult
{
    public string ChainId { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public bool Success { get; set; }

    public int TotalTasks { get; set; }

    public int CompletedTasks { get; set; }

    public int FailedTasks { get; set; }

    public List<TaskContext> Tasks { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public long GetElapsedMilliseconds()
    {
        if (EndTime == null)
            return 0;
        return (long)(EndTime.Value - StartTime).TotalMilliseconds;
    }
}

/// <summary>
/// 任务链执行引擎实现
/// </summary>
public class TaskChainEngine : ITaskChainEngine
{
    private readonly ITaskOrchestrator _orchestrator;
    private readonly ITaskScheduler _scheduler;
    private readonly Dictionary<string, TaskChainResult> _results = new();
    private readonly object _resultLock = new();

    public TaskChainEngine(ITaskOrchestrator orchestrator, ITaskScheduler scheduler)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public async Task<TaskChainResult> ExecuteChainAsync(TaskChain chain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var result = new TaskChainResult
        {
            ChainId = chain.ChainId,
            StartTime = DateTime.UtcNow,
            TotalTasks = chain.Count,
            Tasks = chain.Tasks.ToList()
        };

        lock (_resultLock)
        {
            _results[chain.ChainId] = result;
        }

        try
        {
            if (chain.Mode == ChainExecutionMode.Serial)
            {
                result.Success = await ExecuteSerialInternalAsync(chain.Tasks, result, cancellationToken);
            }
            else if (chain.Mode == ChainExecutionMode.Parallel)
            {
                result.Success = await ExecuteParallelInternalAsync(chain.Tasks, result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            result.EndTime = DateTime.UtcNow;
        }

        return result;
    }

    public async Task<TaskChainResult> ExecuteParallelAsync(IEnumerable<TaskContext> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var taskList = tasks.ToList();
        var result = new TaskChainResult
        {
            StartTime = DateTime.UtcNow,
            TotalTasks = taskList.Count,
            Tasks = taskList
        };

        result.Success = await ExecuteParallelInternalAsync(taskList, result, cancellationToken);
        result.EndTime = DateTime.UtcNow;

        return result;
    }

    public async Task<TaskChainResult> ExecuteSerialAsync(IEnumerable<TaskContext> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var taskList = tasks.ToList();
        var result = new TaskChainResult
        {
            StartTime = DateTime.UtcNow,
            TotalTasks = taskList.Count,
            Tasks = taskList
        };

        result.Success = await ExecuteSerialInternalAsync(taskList, result, cancellationToken);
        result.EndTime = DateTime.UtcNow;

        return result;
    }

    public TaskChainResult? GetChainResult(string chainId)
    {
        lock (_resultLock)
        {
            _results.TryGetValue(chainId, out var result);
            return result;
        }
    }

    private async Task<bool> ExecuteSerialInternalAsync(IEnumerable<TaskContext> tasks, TaskChainResult result, CancellationToken cancellationToken)
    {
        foreach (var task in tasks)
        {
            // 加入调度队列
            await _scheduler.EnqueueAsync(task, cancellationToken);

            // 启动任务
            await _orchestrator.StartTaskAsync(task, cancellationToken);

            // 等待任务完成
            var completedTask = await _orchestrator.WaitTaskAsync(task.TaskId, cancellationToken: cancellationToken);

            if (completedTask?.Status == StateCenter.Models.TaskStatusEnum.Completed)
            {
                result.CompletedTasks++;
            }
            else
            {
                result.FailedTasks++;
                return false; // 顺序执行中断
            }
        }

        return result.FailedTasks == 0;
    }

    private async Task<bool> ExecuteParallelInternalAsync(IEnumerable<TaskContext> tasks, TaskChainResult result, CancellationToken cancellationToken)
    {
        var taskList = tasks.ToList();
        var executionTasks = new List<Task>();

        // 并行启动所有任务
        foreach (var task in taskList)
        {
            await _scheduler.EnqueueAsync(task, cancellationToken);
            await _orchestrator.StartTaskAsync(task, cancellationToken);

            // 添加等待任务
            executionTasks.Add(ExecuteAndTrackAsync(task, result, cancellationToken));
        }

        // 等待所有任务完成
        await Task.WhenAll(executionTasks).ConfigureAwait(false);

        return result.FailedTasks == 0;
    }

    private async Task ExecuteAndTrackAsync(TaskContext task, TaskChainResult result, CancellationToken cancellationToken)
    {
        var completedTask = await _orchestrator.WaitTaskAsync(task.TaskId, cancellationToken: cancellationToken);

        if (completedTask?.Status == StateCenter.Models.TaskStatusEnum.Completed)
        {
            lock (_resultLock)
            {
                result.CompletedTasks++;
            }
        }
        else
        {
            lock (_resultLock)
            {
                result.FailedTasks++;
            }
        }
    }
}
