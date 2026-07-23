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

    public TaskExecutionWorker(
        ITaskScheduler scheduler,
        ITaskOrchestrator orchestrator,
        ICommandCenter commandCenter,
        ISqlSugarClient? db,
        ILogger<TaskExecutionWorker> logger)
    {
        _scheduler = scheduler;
        _orchestrator = orchestrator;
        _commandCenter = commandCenter;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not let a task already queued by the simulator run synchronously on the
        // generic host startup path. The worker must never delay Kestrel binding.
        await Task.Yield();
        _logger.LogInformation("TaskExecutionWorker 启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(stoppingToken);
                if (task == null) { await Task.Delay(500, stoppingToken); continue; }

                var palletId = task.Tags.GetValueOrDefault("PalletId") ?? task.TaskId;
                var fromNode = task.Tags.GetValueOrDefault("SourceNode") ?? task.DeviceId;
                var toNode = task.Tags.GetValueOrDefault("TargetNode") ?? "ASRS01";

                _logger.LogInformation("[Worker] ▶ {TaskId}", task.TaskId);

                await _orchestrator.StartTaskAsync(task, stoppingToken);
                await LogEventAsync(task.TaskId, "TaskRunning", "started");

                await Task.Delay(3000, stoppingToken);
                await ExecuteTaskAsync(task, stoppingToken);

                await _orchestrator.CompleteTaskAsync(task.TaskId, true);
                await LogEventAsync(task.TaskId, "TaskCompleted", "completed");
                await ArchiveTaskAsync(task, true, palletId, fromNode, toNode);

                _scheduler.ReleaseDeviceSlot(task.DeviceId);
                _logger.LogInformation("[Worker] ✅ {TaskId} 完成", task.TaskId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker] 异常");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ExecuteTaskAsync(TaskContext task, CancellationToken ct)
    {
        var d = task.DeviceId;
        await Task.Delay(2000, ct);

        // 直接写入，CommandCenter 根据命令的 [PlcStruct] / [PlcBlock] 自动路由协议
        await _commandCenter.SendTagCommandAsync(d, "ExecuteTask",
            new TagControlCommand { StartStation1 = true, SpeedSetpoint1 = 1200 }, task.TaskId, ct);

        _logger.LogInformation("[Worker] ⚡ {Device} → ExecuteTask", d);
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
