namespace Wcs.Core.RuleEngine;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 任务生成器 — 监听业务信号事件，通过规则引擎生成任务并提交到调度器
///
/// 完整链路：
/// PLC → SignalMapper → BusinessSignals → EventBus → TaskGenerator → TaskScheduler → 执行
///                                                                       ↑
///                                                               RuleEngine 匹配规则
/// </summary>
public class TaskGenerator : IEventHandler<ConveyorReadyChangedEvent>,
    IEventHandler<PalletArrivedEvent>,
    IEventHandler<DeviceFaultEvent>
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<TaskGenerator>? _logger;

    public TaskGenerator(
        IRuleEngine ruleEngine,
        ITaskScheduler scheduler,
        ILogger<TaskGenerator>? logger = null)
    {
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger;
    }

    /// <summary>
    /// 处理输送线就绪信号
    /// </summary>
    public async Task HandleAsync(ConveyorReadyChangedEvent @event, CancellationToken ct)
    {
        _logger?.LogDebug("TaskGenerator: evaluating ConveyorReadyChangedEvent for {DeviceId}", @event.DeviceId);
        await EvaluateAndSubmit(@event, ct);
    }

    /// <summary>
    /// 处理托盘到位信号
    /// </summary>
    public async Task HandleAsync(PalletArrivedEvent @event, CancellationToken ct)
    {
        _logger?.LogDebug("TaskGenerator: evaluating PalletArrivedEvent for {DeviceId}", @event.DeviceId);
        await EvaluateAndSubmit(@event, ct);
    }

    /// <summary>
    /// 处理设备故障信号
    /// </summary>
    public async Task HandleAsync(DeviceFaultEvent @event, CancellationToken ct)
    {
        _logger?.LogDebug("TaskGenerator: evaluating DeviceFaultEvent for {DeviceId}", @event.DeviceId);
        await EvaluateAndSubmit(@event, ct);
    }

    /// <summary>
    /// 评估通用信号 — 供 SignalBus 回调使用
    /// </summary>
    public async Task EvaluateSignalAsync(object signalEvent, CancellationToken ct)
    {
        await EvaluateAndSubmit(signalEvent, ct);
    }

    private async Task EvaluateAndSubmit(object signalEvent, CancellationToken ct)
    {
        try
        {
            var tasks = _ruleEngine.Evaluate(signalEvent);

            foreach (var task in tasks)
            {
                _logger?.LogInformation(
                    "TaskGenerator: rule matched — generated task {TaskId} (Device={DeviceId}, Priority={Priority})",
                    task.TaskId, task.DeviceId, task.Priority);

                await _scheduler.EnqueueAsync(task, ct);
            }

            if (tasks.Count > 0)
            {
                _logger?.LogInformation("TaskGenerator: submitted {Count} tasks from signal {SignalType}",
                    tasks.Count, signalEvent.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TaskGenerator: error evaluating signal {SignalType}",
                signalEvent.GetType().Name);
        }
    }
}
