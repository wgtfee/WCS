namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.CommandCenter;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;

public class TaskExecutionWorker : BackgroundService
{
    private readonly ITaskScheduler _scheduler;
    private readonly ITaskOrchestrator _orchestrator;
    private readonly ICommandCenter _commandCenter;
    private readonly ISqlSugarClient? _db;
    private readonly ILogger<TaskExecutionWorker> _logger;
    private readonly int _workerCount;
    private readonly int _idleDelayMs;
    private readonly int _executionDelayMs;

    public TaskExecutionWorker(
        ITaskScheduler scheduler,
        ITaskOrchestrator orchestrator,
        ICommandCenter commandCenter,
        ISqlSugarClient? db,
        IConfiguration configuration,
        ILogger<TaskExecutionWorker> logger)
    {
        _scheduler = scheduler;
        _orchestrator = orchestrator;
        _commandCenter = commandCenter;
        _db = db;
        _logger = logger;
        _workerCount = Math.Clamp(configuration.GetValue("TaskExecution:WorkerCount", 16), 1, 64);
        _idleDelayMs = Math.Clamp(configuration.GetValue("TaskExecution:IdleDelayMs", 25), 5, 1000);
        _executionDelayMs = Math.Max(0, configuration.GetValue("TaskExecution:ExecutionDelayMs", 5000));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not let a task already queued by the simulator run synchronously on the
        // generic host startup path. The worker must never delay Kestrel binding.
        await Task.Yield();
        _logger.LogInformation(
            "TaskExecutionWorker 启动: workers={WorkerCount}, executionDelayMs={ExecutionDelayMs}",
            _workerCount,
            _executionDelayMs);

        var workers = Enumerable.Range(1, _workerCount)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(stoppingToken);
                if (task == null)
                {
                    await Task.Delay(_idleDelayMs, stoppingToken);
                    continue;
                }

                await ProcessTaskAsync(workerId, task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker-{WorkerId}] 调度循环异常", workerId);
                await Task.Delay(Math.Max(100, _idleDelayMs), stoppingToken);
            }
        }
    }

    private async Task ProcessTaskAsync(int workerId, TaskContext task, CancellationToken stoppingToken)
    {
        var palletId = task.Tags.GetValueOrDefault("PalletId") ?? task.TaskId;
        var fromNode = task.Tags.GetValueOrDefault("SourceNode") ?? task.DeviceId;
        var toNode = task.Tags.GetValueOrDefault("TargetNode") ?? "ASRS01";
        var started = false;
        var terminal = false;

        try
        {
            _logger.LogInformation("[Worker-{WorkerId}] ▶ {TaskId}", workerId, task.TaskId);

            started = await _orchestrator.StartTaskAsync(task, stoppingToken);
            if (!started)
                throw new InvalidOperationException($"任务 {task.TaskId} 无法进入运行状态");

            await LogEventAsync(task.TaskId, "TaskRunning", "started");

            // 真实现场应由 PLC 完成/到位反馈结束任务；该延迟只用于当前示例执行器与隔离压测。
            if (_executionDelayMs > 0)
                await Task.Delay(_executionDelayMs, stoppingToken);

            await ExecuteTaskAsync(task, stoppingToken);

            await _orchestrator.CompleteTaskAsync(
                task.TaskId,
                true,
                cancellationToken: stoppingToken);
            await LogEventAsync(task.TaskId, "TaskCompleted", "completed");
            await ArchiveTaskAsync(task, true, palletId, fromNode, toNode);
            terminal = true;

            _logger.LogInformation("[Worker-{WorkerId}] ✅ {TaskId} 完成", workerId, task.TaskId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 保留 Running 状态给恢复流程处理，不在关机过程中伪造失败归档。
            if (!started)
                _scheduler.ReleaseDeviceSlot(task.DeviceId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Worker-{WorkerId}] 任务 {TaskId} 异常", workerId, task.TaskId);

            if (started)
            {
                await _orchestrator.CompleteTaskAsync(
                    task.TaskId,
                    false,
                    ex.Message,
                    cancellationToken: CancellationToken.None);
            }
            else
            {
                // Dequeue 已占用设备槽，但 StartTask 尚未登记到 Orchestrator。
                _scheduler.ReleaseDeviceSlot(task.DeviceId);
                task.Status = TaskStatusEnum.Failed;
                task.EndTime = DateTime.UtcNow;
                task.ErrorMessage = ex.Message;
            }

            await LogEventAsync(task.TaskId, "TaskFailed", ex.Message);
            await ArchiveTaskAsync(task, false, palletId, fromNode, toNode);
            terminal = true;
        }
        finally
        {
            // CompleteTaskAsync 已负责释放设备槽，不能再次 Release，否则并行时会破坏限流计数。
            if (terminal)
                _scheduler.Remove(task.TaskId);
        }
    }

    private async Task ExecuteTaskAsync(TaskContext task, CancellationToken ct)
    {
        var deviceId = task.DeviceId;

        // 直接写入，CommandCenter 根据命令的 [PlcStruct] / [PlcBlock] 自动路由协议
        await _commandCenter.SendTagCommandAsync(
            deviceId,
            "ExecuteTask",
            new TagControlCommand { StartStation1 = true, SpeedSetpoint1 = 1200 },
            task.TaskId,
            ct);

        _logger.LogInformation("[Worker] ⚡ {Device} → ExecuteTask", deviceId);
    }

    /// <summary>写入 Wcs_TaskEvent（SqlSugar）</summary>
    private async Task LogEventAsync(string taskId, string eventType, string payload)
    {
        if (_db == null) return;
        try
        {
            // Background jobs do not have an HTTP async context. SqlSugarScope
            // requires an isolated copy for this usage.
            var db = _db.CopyNew();
            await db.Insertable(new TaskEventEntity
            {
                TaskId = taskId,
                EventType = eventType,
                Payload = payload,
                CreateTime = DateTime.UtcNow
            }).ExecuteCommandAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "写入 TaskEvent 失败"); }
    }

    /// <summary>写入 TaskHistory + Wcs_TaskRun + Wcs_TransportHistory</summary>
    private async Task ArchiveTaskAsync(TaskContext task, bool success, string palletId,
        string fromNode, string toNode)
    {
        if (_db == null) return;
        var now = DateTime.UtcNow;
        try
        {
            // Keep the whole archive operation on one job-local client.
            var db = _db.CopyNew();

            // Wcs_TaskHistory
            await db.Insertable(new TaskHistoryEntity
            {
                TaskId = task.TaskId,
                RouteId = task.RouteId,
                Priority = task.Priority,
                StartTime = task.StartTime,
                EndTime = task.EndTime ?? now,
                Success = success,
                ErrorMessage = success ? null : task.ErrorMessage
            }).ExecuteCommandAsync();

            // Wcs_TaskRun
            await db.Insertable(new TaskRunEntity
            {
                TaskId = task.TaskId,
                DeviceId = task.DeviceId, RouteId = task.RouteId, PalletId = palletId,
                Status = (int)(success ? TaskStatusEnum.Completed : TaskStatusEnum.Failed),
                Priority = task.Priority, CreatedTime = task.CreatedTime,
                StartTime = task.StartTime, EndTime = task.EndTime ?? now,
                ErrorMessage = success ? null : task.ErrorMessage, RetryCount = task.RetryCount
            }).ExecuteCommandAsync();

            // Wcs_TransportHistory
            await db.Insertable(new TransportHistoryEntity
            {
                TaskId = task.TaskId, PalletId = palletId,
                SourceNode = fromNode, TargetNode = toNode, Route = task.RouteId,
                StartTime = task.StartTime ?? now, EndTime = task.EndTime ?? now,
                Success = success, FailureReason = success ? null : task.ErrorMessage,
                TotalDurationMs = task.StartTime.HasValue
                    ? (long)(now - task.StartTime.Value).TotalMilliseconds : 0
            }).ExecuteCommandAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "归档失败"); }
    }
}
