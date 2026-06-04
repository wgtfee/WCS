namespace Wcs.Host.BackgroundServices;

using Wcs.Core.CommandCenter;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 任务执行工人 — 从 TaskScheduler 出队任务并执行
///
/// 执行流程：
///   TaskScheduler.DequeueAsync() → 获取任务
///     → TaskOrchestrator.StartTaskAsync() → 标记 Running
///     → (模拟运输耗时)
///     → PlcWriter.WriteStructAsync() → 写入 PLC ← 你关心的写入时机！
///     → TaskOrchestrator.CompleteTaskAsync() → 标记 Completed
///
/// 每秒轮询一次任务队列，有任务则执行
/// </summary>
public class TaskExecutionWorker : BackgroundService
{
    private readonly ITaskScheduler _scheduler;
    private readonly ITaskOrchestrator _orchestrator;
    private readonly ICommandCenter _commandCenter;
    private readonly ILogger<TaskExecutionWorker> _logger;

    public TaskExecutionWorker(
        ITaskScheduler scheduler,
        ITaskOrchestrator orchestrator,
        ICommandCenter commandCenter,
        ILogger<TaskExecutionWorker> logger)
    {
        _scheduler = scheduler;
        _orchestrator = orchestrator;
        _commandCenter = commandCenter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskExecutionWorker 启动 — 开始轮询任务队列");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(stoppingToken);
                if (task == null)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                _logger.LogInformation("[Worker] ▶ 开始执行 {TaskId} ({Route})", task.TaskId, task.RouteId);

                // 1. 标记任务开始
                await _orchestrator.StartTaskAsync(task, stoppingToken);

                // 2. 根据任务来源设备发送 PLC 命令
                await ExecuteTaskAsync(task, stoppingToken);

                // 3. 标记任务完成
                await _orchestrator.CompleteTaskAsync(task.TaskId, true);

                _logger.LogInformation("[Worker] ✅ {TaskId} 完成", task.TaskId);

                // 释放设备并发槽位
                _scheduler.ReleaseDeviceSlot(task.DeviceId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker] 执行异常");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 执行任务 — 根据不同设备类型发送不同命令写入 PLC
    /// 这里就是你要看的 "什么时候写入 PLC"
    /// </summary>
    private async Task ExecuteTaskAsync(TaskContext task, CancellationToken ct)
    {
        var deviceId = task.DeviceId;
        var isRecovery = task.Category == TaskCategory.Recovery;

        // 模拟运输耗时（2~5 秒）
        var delay = isRecovery ? 2000 : 3000;
        _logger.LogInformation("[Worker]   ⏳ {Device} 运输中...({Delay}ms)", deviceId, delay);
        await Task.Delay(delay, ct);

        // ===== 写入 PLC — 这就是你关心的写入时机！=====
        // 根据设备类型发送不同的命令到不同的 PLC

        if (deviceId.StartsWith("CV"))
        {
            // 输送线 → 写入 PLC1.DB101
            _logger.LogInformation("[Worker]   ⚡ {Device} → 写入 PLC1.DB101 (启动输送机)", deviceId);
            var cmd = new ConveyorControlCommand
            {
                StartStation1 = true,
                SpeedSetpoint1 = 1200
            };
            await _commandCenter.SendStructuredCommandAsync(deviceId, "StartConveyor", cmd, task.TaskId, ct);
            _logger.LogInformation("[Worker]   ✅ {Device} PLC 写入完成", deviceId);
        }
        else if (deviceId.StartsWith("LIFT"))
        {
            // 提升机 → 写入 PLC1.DB102
            _logger.LogInformation("[Worker]   ⚡ {Device} → 写入 PLC1.DB102 (启动提升机)", deviceId);
            var cmd = new LiftCommand { GoUp = true, TargetFloor = 2 };
            await _commandCenter.SendStructuredCommandAsync(deviceId, "LiftUp", cmd, task.TaskId, ct);
        }
        else if (deviceId.StartsWith("ASRS"))
        {
            // 堆垛机 → 写入 PLC2.DB201
            _logger.LogInformation("[Worker]   ⚡ {Device} → 写入 PLC2.DB201 (堆垛机入库)", deviceId);
            var cmd = new StackerControlCommand { StoreCmd1 = true, TargetCol1 = 15, TargetRow1 = 8 };
            await _commandCenter.SendStructuredCommandAsync(deviceId, "Store", cmd, task.TaskId, ct);
        }
        else if (deviceId.StartsWith("ROBOT"))
        {
            // 机器人 → 写入 PLC3.DB101
            _logger.LogInformation("[Worker]   ⚡ {Device} → 写入 PLC3.DB101 (机器人抓取)", deviceId);
            var cmd = new RobotControlCommand { GripCmd1 = true, TargetPos1 = 3 };
            await _commandCenter.SendStructuredCommandAsync(deviceId, "Grip", cmd, task.TaskId, ct);
        }
        else
        {
            _logger.LogInformation("[Worker]   ⚡ {Device} → 通用命令", deviceId);
            await Task.Delay(1000, ct);
        }
    }
}
