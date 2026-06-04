namespace Wcs.Host.BackgroundServices;

using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 任务生成服务 — 订阅 EventBus 中的 PalletArrivedEvent
/// 自动生成运输任务并提交到 TaskScheduler
///
/// 这是"PLC 信号 → 业务任务"的关键转换层
/// </summary>
public class TaskGeneratorService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<TaskGeneratorService> _logger;
    private long _taskCounter;

    public TaskGeneratorService(
        IEventBus eventBus,
        ITaskScheduler scheduler,
        ILogger<TaskGeneratorService> logger)
    {
        _eventBus = eventBus;
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 订阅 PalletArrivedEvent → 生成运输任务
        _eventBus.Subscribe<PalletArrivedEvent>(async (evt, ct) =>
        {
            var taskId = $"T{++_taskCounter:D5}";
            var palletId = evt.Barcode ?? $"PALLET_{_taskCounter:D6}";

            var task = new TaskContext
            {
                TaskId = taskId,
                DeviceId = evt.DeviceId,
                Priority = 2,
                PriorityLevel = TaskPriority.Normal,
                Category = TaskCategory.Production,
                RouteId = $"{evt.DeviceId}→ASRS01",
                Tags =
                {
                    ["PalletId"] = palletId,
                    ["SourceNode"] = evt.DeviceId,
                    ["TargetNode"] = "ASRS01",
                    ["Simulated"] = "true"
                }
            };
            task.Parameters["PalletId"] = palletId;
            task.Parameters["FromNode"] = evt.DeviceId;

            await _scheduler.EnqueueAsync(task, ct);

            _logger.LogInformation("[TaskGen] 📦 {TaskId}: {Pallet} → {Route} (队列={Queue})",
                taskId, palletId, task.RouteId, _scheduler.GetQueueCount());
        });

        // 订阅 DeviceFaultEvent → 生成恢复任务
        _eventBus.Subscribe<DeviceFaultEvent>(async (evt, ct) =>
        {
            var taskId = $"R{++_taskCounter:D5}";
            var task = new TaskContext
            {
                TaskId = taskId,
                DeviceId = evt.DeviceId,
                Priority = 4,
                PriorityLevel = TaskPriority.Emergency,
                Category = TaskCategory.Recovery,
                RouteId = $"RECOVERY→{evt.DeviceId}",
                Tags = { ["FaultCode"] = evt.FaultCode, ["Recovery"] = "true" }
            };

            await _scheduler.EnqueueAsync(task, ct);

            _logger.LogWarning("[TaskGen] 🔧 恢复任务 {TaskId}: {Device} 故障 {FaultCode}",
                taskId, evt.DeviceId, evt.FaultCode);
        });

        _logger.LogInformation("TaskGeneratorService 已启动 — 监听 PalletArrivedEvent → 生成任务");
        return Task.CompletedTask;
    }
}
