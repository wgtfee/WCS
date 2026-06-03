namespace Wcs.Core.RuleEngine;

using System.Collections.Concurrent;
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

    // V8: 信号幂等窗口 — 相同信号 5 秒内只处理一次
    private readonly ConcurrentDictionary<string, DateTime> _idempotencyCache = new();
    private readonly TimeSpan _idempotencyWindow = TimeSpan.FromSeconds(5);

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
            // V8: 幂等窗口 — 防止连续重复信号生成多个任务
            if (IsDuplicateSignal(signalEvent))
            {
                _logger?.LogDebug("TaskGenerator: duplicate signal {SignalType} ignored (idempotency window {Window}s)",
                    signalEvent.GetType().Name, _idempotencyWindow.TotalSeconds);
                return;
            }

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

    /// <summary>
    /// V8: 检查信号是否在幂等窗口内（5 秒内相同信号只处理一次）
    /// </summary>
    private bool IsDuplicateSignal(object signalEvent)
    {
        var signalKey = $"{signalEvent.GetType().Name}:{signalEvent.GetHashCode()}";
        var now = DateTime.UtcNow;
        if (_idempotencyCache.TryGetValue(signalKey, out var lastTime) && (now - lastTime) < _idempotencyWindow)
            return true;
        _idempotencyCache[signalKey] = now;
        CleanupIdempotencyCache();
        return false;
    }

    private void CleanupIdempotencyCache()
    {
        var cutoff = DateTime.UtcNow.Subtract(_idempotencyWindow);
        foreach (var kvp in _idempotencyCache)
            if (kvp.Value < cutoff) _idempotencyCache.TryRemove(kvp.Key, out _);
    }
}
