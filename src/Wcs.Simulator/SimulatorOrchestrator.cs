namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;
using Wcs.Simulator.DeviceSimulator;
using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 模拟编排器 — 创建完整的模拟执行闭环
///
/// 闭环链路：
/// TransportGenerator → TaskScheduler
///     → Orchestrator 出队任务 → 沿路径触发 DeviceSimulator
///     → DeviceSimulator 发出信号到 SimulatorSignalSource
///     → Orchestrator 读取信号 → 发布到 EventBus
///     → StateCenter + PersistBackgroundService → DB
/// </summary>
public class SimulatorOrchestrator
{
    private readonly VirtualPlant _plant;
    private readonly ITaskScheduler _scheduler;
    private readonly IEventBus _eventBus;
    private readonly IStateCenter? _stateCenter;
    private readonly ILogger<SimulatorOrchestrator>? _logger;
    private bool _running;
    private long _executed;
    private long _completed;
    private long _signalsBridged;

    private static readonly List<string> DefaultRoute = new()
    {
        "RECV_DOCK", "CV01", "CV02", "LIFT01", "CV03", "ASRS01"
    };

    private readonly Dictionary<string, DeviceSimulatorBase> _deviceMap = new();

    public long Executed => Interlocked.Read(ref _executed);
    public long Completed => Interlocked.Read(ref _completed);
    public long SignalsBridged => Interlocked.Read(ref _signalsBridged);

    public SimulatorOrchestrator(
        VirtualPlant plant,
        ITaskScheduler scheduler,
        IEventBus eventBus,
        IStateCenter? stateCenter = null,
        ILogger<SimulatorOrchestrator>? logger = null)
    {
        _plant = plant;
        _scheduler = scheduler;
        _eventBus = eventBus;
        _stateCenter = stateCenter;
        _logger = logger;

        foreach (var cv in plant.Conveyors) _deviceMap[cv.DeviceId] = cv;
        foreach (var l in plant.Lifts) _deviceMap[l.DeviceId] = l;
        foreach (var a in plant.AsrsMachines) _deviceMap[a.DeviceId] = a;
        foreach (var r in plant.Robots) _deviceMap[r.DeviceId] = r;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogInformation("===========================================");
        _logger?.LogInformation(" SimulatorOrchestrator 启动 — 3 线程闭环");
        _logger?.LogInformation(" 设备数: {Count}, 映射设备: {Mapped}",
            _plant.DeviceCount, _deviceMap.Count);
        _logger?.LogInformation("===========================================");

        var tasks = new[]
        {
            Task.Run(() => SignalReaderLoopAsync(ct), ct),
            Task.Run(() => TaskExecutorLoopAsync(ct), ct),
            Task.Run(() => MetricsLogLoopAsync(ct), ct),
        };

        await Task.WhenAny(tasks);
        _running = false;
    }

    public void Stop() => _running = false;

    // ==========================
    // 循环 1: 模拟信号 → EventBus
    // ==========================
    private async Task SignalReaderLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("[信号桥接] 线程启动: SimulatorSignalSource → EventBus");

        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var signals = await _plant.SignalSource.ReadAsync(ct);
                if (signals.Count > 0)
                {
                    _logger?.LogInformation("[信号桥接] 读取到 {Count} 个模拟信号", signals.Count);
                }
                foreach (var signal in signals)
                {
                    var wcsEvent = ConvertToEvent(signal);
                    await _eventBus.PublishAsync(wcsEvent, ct);
                    Interlocked.Increment(ref _signalsBridged);
                    _logger?.LogInformation("[信号桥接] ⚡ {SignalId}={Value} → EventBus (累计 {Total})",
                        signal.SignalId, signal.Value, _signalsBridged);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[信号桥接] 异常");
            }
            await Task.Delay(50, ct);
        }
    }

    // ==========================
    // 循环 2: 出队 + 模拟运输（并发度 5）
    // ==========================
    private async Task TaskExecutorLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("[任务执行] 线程启动: TaskScheduler → DeviceSimulator (并发度=5)");
        var semaphore = new SemaphoreSlim(5);

        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(ct);
                if (task == null)
                {
                    var q = _scheduler.GetQueueCount();
                    if (q > 0)
                        _logger?.LogInformation("[任务执行] 队列 {Count} 个任务但 Dequeue=null (设备并发限制)", q);
                    await Task.Delay(500, ct);
                    continue;
                }

                Interlocked.Increment(ref _executed);
                var palletId = task.Tags.TryGetValue("PalletId", out var p) ? p : "?";
                _logger?.LogInformation("[任务执行] ▶ #{Exec}  {TaskId}  Pallet={Pallet}  Route={Route}  队列={Queue}",
                    _executed, task.TaskId, palletId, task.RouteId, _scheduler.GetQueueCount());

                await semaphore.WaitAsync(ct);
                _ = ExecuteWithSemaphore(semaphore, task, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[任务执行] 出队异常");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ExecuteWithSemaphore(SemaphoreSlim semaphore, TaskContext task, CancellationToken ct)
    {
        try
        {
            await SimulateTransportAsync(task, ct);
            _scheduler.ReleaseDeviceSlot(task.DeviceId);
            _logger?.LogInformation("[任务执行] ✅ 释放设备槽 {Device}", task.DeviceId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[任务执行] 🔴 任务执行异常 {TaskId}", task.TaskId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// 模拟运输 + 发事件到 EventBus 确保持久化
    /// </summary>
    private async Task SimulateTransportAsync(TaskContext task, CancellationToken ct)
    {
        var palletId = task.Tags.TryGetValue("PalletId", out var p) ? p : task.TaskId;
        _logger?.LogInformation("[运输] 🚚 启动: Pallet={Pallet}  Route={Route}", palletId, task.RouteId);

        // Phase 1: Running → StateCenter + EventBus
        _logger?.LogInformation("[运输] Phase1: 标记 Running → UpdateTaskRuntime + Publish TaskStateChangedEvent");
        _stateCenter?.UpdateTaskRuntime(task.TaskId, new TaskRuntime
        {
            TaskId = task.TaskId,
            Status = TaskStatusEnum.Running,
            StartTime = DateTime.UtcNow
        });
        await _eventBus.PublishAsync(new TaskStateChangedEvent
        {
            TaskId = task.TaskId,
            NewStatus = TaskStatusEnum.Running,
            OldStatus = TaskStatusEnum.Created
        }, ct);
        _logger?.LogInformation("[运输] ✅ Running 事件已发布 → EventBus");

        // Phase 2: 逐个经过路径节点
        for (int i = 0; i < DefaultRoute.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var nodeId = DefaultRoute[i];

            if (_deviceMap.TryGetValue(nodeId, out var device))
            {
                _logger?.LogInformation("[运输]   Step {I}/{Total}: {Dev} 启动 (耗时 {T}ms)",
                    i + 1, DefaultRoute.Count, nodeId, device.TransportTimeMs);
                await device.StartAsync(ct);
                _logger?.LogInformation("[运输]   Step {I}/{Total}: {Dev} ✅ 完成, 信号已入模拟队列",
                    i + 1, DefaultRoute.Count, nodeId);
            }
            else
            {
                _logger?.LogInformation("[运输]   Step {I}/{Total}: {Node} (非设备, 模拟 1s)", i + 1, DefaultRoute.Count, nodeId);
                await Task.Delay(1000, ct);
            }
        }

        // Phase 3: Completed → StateCenter + EventBus
        _stateCenter?.UpdateTaskRuntime(task.TaskId, new TaskRuntime
        {
            TaskId = task.TaskId,
            Status = TaskStatusEnum.Completed,
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow
        });
        await _eventBus.PublishAsync(new TaskStateChangedEvent
        {
            TaskId = task.TaskId,
            NewStatus = TaskStatusEnum.Completed,
            OldStatus = TaskStatusEnum.Running
        }, ct);

        Interlocked.Increment(ref _completed);
        _logger?.LogInformation("[运输] ✅ 完成: Pallet={Pallet}  Route={Route}  累计完成={Completed}",
            palletId, task.RouteId, _completed);
    }

    // ==========================
    // 循环 3: 指标日志（15s）
    // ==========================
    private async Task MetricsLogLoopAsync(CancellationToken ct)
    {
        long lastGen = 0, lastExec = 0, lastComp = 0, lastSig = 0;
        while (!ct.IsCancellationRequested && _running)
        {
            await Task.Delay(15000, ct);
            var gen = _plant.Generator.Generated;
            var exec = Interlocked.Read(ref _executed);
            var comp = Interlocked.Read(ref _completed);
            var sig = Interlocked.Read(ref _signalsBridged);
            var q = _scheduler.GetQueueCount();

            _logger?.LogInformation("====== [指标 15s] ======");
            _logger?.LogInformation("生成:{Gen}  (TPS:{Tps})  出队:{Exec}  完成:{Comp}  信号:{Sig}  队列:{Q}",
                gen, (gen - lastGen) / 15, exec, comp, sig, q);

            if (gen > 0 && exec == 0 && comp == 0)
                _logger?.LogWarning("⚠️  任务已生成但未出队！检查 TaskScheduler.DequeueAsync");
            if (exec > 0 && comp == 0)
                _logger?.LogWarning("⚠️  任务已出队但未完成！");

            lastGen = gen; lastExec = exec; lastComp = comp; lastSig = sig;
        }
    }

    private static IEvent ConvertToEvent(SignalChangedEvent signal)
    {
        var deviceId = signal.SignalId.Contains('.')
            ? signal.SignalId.Split('.')[0]
            : signal.SignalId;

        return new DeviceStateChangedEvent
        {
            DeviceId = deviceId,
            NewStatus = DeviceStatusEnum.Running,
            OldStatus = DeviceStatusEnum.Idle
        };
    }
}
