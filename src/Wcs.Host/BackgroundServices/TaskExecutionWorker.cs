namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.CommandCenter;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.Persistence.Repositories;

public class TaskExecutionWorker : BackgroundService
{
    private readonly ITaskScheduler _scheduler;
    private readonly ITaskOrchestrator _orchestrator;
    private readonly ICommandCenter _commandCenter;
    private readonly ISqlSugarClient? _db;
    private readonly string _connStr;
    private readonly ILogger<TaskExecutionWorker> _logger;

    public TaskExecutionWorker(
        ITaskScheduler scheduler,
        ITaskOrchestrator orchestrator,
        ICommandCenter commandCenter,
        ISqlSugarClient? db,
        IConfiguration config,
        ILogger<TaskExecutionWorker> logger)
    {
        _scheduler = scheduler;
        _orchestrator = orchestrator;
        _commandCenter = commandCenter;
        _db = db;
        _connStr = config.GetConnectionString("WcsDb") ?? "";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskExecutionWorker 启动 — 轮询任务队列");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(stoppingToken);
                if (task == null) { await Task.Delay(500, stoppingToken); continue; }

                var palletId = task.Tags.GetValueOrDefault("PalletId") ?? task.TaskId;
                var fromNode = task.Tags.GetValueOrDefault("SourceNode") ?? task.DeviceId;
                var toNode = task.Tags.GetValueOrDefault("TargetNode") ?? "ASRS01";

                _logger.LogInformation("[Worker] ▶ {TaskId} ({Route})", task.TaskId, task.RouteId);

                // == 1. 标记运行 ==
                await _orchestrator.StartTaskAsync(task, stoppingToken);
                await LogTaskEventAsync(task.TaskId, "TaskRunning", "Started execution");

                // == 2. 模拟运输 ==
                await Task.Delay(3000, stoppingToken);

                // == 3. 写入 PLC ==
                await ExecuteTaskAsync(task, stoppingToken);

                // == 4. 标记完成 ==
                await _orchestrator.CompleteTaskAsync(task.TaskId, true);
                await LogTaskEventAsync(task.TaskId, "TaskCompleted", "Completed successfully");

                // == 5. 写入历史 ==
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

        if (d.StartsWith("CV"))
        {
            _logger.LogInformation("[Worker] ⚡ {Device} → PLC1.DB101", d);
            await _commandCenter.SendStructuredCommandAsync(d, "StartConveyor",
                new ConveyorControlCommand { StartStation1 = true, SpeedSetpoint1 = 1200 }, task.TaskId, ct);
        }
        else if (d.StartsWith("LIFT"))
        {
            _logger.LogInformation("[Worker] ⚡ {Device} → PLC1.DB102", d);
            var liftCmd = (Wcs.Core.PlcSubsystem.Examples.LiftCommand)
                Activator.CreateInstance(typeof(Wcs.Core.PlcSubsystem.Examples.LiftCommand))!;
            await _commandCenter.SendStructuredCommandAsync(d, "LiftUp", liftCmd, task.TaskId, ct);
        }
        else if (d.StartsWith("ASRS"))
        {
            _logger.LogInformation("[Worker] ⚡ {Device} → PLC2.DB201", d);
            await _commandCenter.SendStructuredCommandAsync(d, "Store",
                new StackerControlCommand { StoreCmd1 = true }, task.TaskId, ct);
        }
        else if (d.StartsWith("ROBOT"))
        {
            _logger.LogInformation("[Worker] ⚡ {Device} → PLC3.DB101", d);
            await _commandCenter.SendStructuredCommandAsync(d, "Grip",
                new RobotControlCommand { GripCmd1 = true }, task.TaskId, ct);
        }
    }

    /// <summary>写入 TaskEvents（EF Core 表）</summary>
    private async Task LogTaskEventAsync(string taskId, string eventType, string payload)
    {
        try
        {
            var repo = new TaskEventRepository(_connStr);
            await repo.AppendAsync(new TaskEventEntity
            {
                TaskId = taskId,
                EventType = eventType,
                Payload = payload,
                CreateTime = DateTime.UtcNow
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "写入 TaskEvents 失败"); }
    }

    /// <summary>写入 TaskHistories + Wcs_TaskRun + Wcs_TransportHistory</summary>
    private async Task ArchiveTaskAsync(TaskContext task, bool success, string palletId,
        string fromNode, string toNode)
    {
        try
        {
            // EF Core TaskHistories
            var repo = new TaskRepository(_connStr);
            await repo.ArchiveTaskAsync(new TaskHistoryEntity
            {
                TaskId = task.TaskId,
                RouteId = task.RouteId,
                Priority = task.Priority,
                StartTime = task.StartTime,
                EndTime = task.EndTime,
                Success = success,
                ErrorMessage = task.ErrorMessage
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "写入 TaskHistories 失败"); }

        if (_db == null) return;

        try
        {
            // SqlSugar Wcs_TaskRun
            await _db.Insertable(new TaskRunEntity
            {
                TaskId = task.TaskId,
                DeviceId = task.DeviceId,
                RouteId = task.RouteId,
                PalletId = palletId,
                Status = (int)(success ? Wcs.Core.StateCenter.Models.TaskStatusEnum.Completed : Wcs.Core.StateCenter.Models.TaskStatusEnum.Failed),
                Priority = task.Priority,
                CreatedTime = task.CreatedTime,
                StartTime = task.StartTime,
                EndTime = task.EndTime,
                ErrorMessage = success ? null : task.ErrorMessage,
                RetryCount = task.RetryCount
            }).ExecuteCommandAsync();

            // SqlSugar Wcs_TransportHistory
            await _db.Insertable(new TransportHistoryEntity
            {
                TaskId = task.TaskId,
                PalletId = palletId,
                SourceNode = fromNode,
                TargetNode = toNode,
                Route = task.RouteId,
                StartTime = task.StartTime ?? DateTime.UtcNow,
                EndTime = task.EndTime,
                Success = success,
                FailureReason = success ? null : task.ErrorMessage,
                TotalDurationMs = task.StartTime.HasValue ? (long)(DateTime.UtcNow - task.StartTime.Value).TotalMilliseconds : 0
            }).ExecuteCommandAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "写入 Wcs_TaskRun/TransportHistory 失败"); }
    }
}
