namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Interfaces;
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
///     → Orchestrator 读取信号 → 发布到 SignalBus
///     → RuleEngine → TaskGenerator → StateCenter → DB
/// </summary>
public class SimulatorOrchestrator
{
    private readonly VirtualPlant _plant;
    private readonly ITaskScheduler _scheduler;
    private readonly IEventBus _signalBus;
    private readonly IStateCenter? _stateCenter;
    private readonly ILogger<SimulatorOrchestrator>? _logger;
    private bool _running;

    // 预定义路径模板: (起点 → 途经设备 → 终点)
    private static readonly List<string> DefaultRoute = new()
    {
        "RECV_DOCK", "CV01", "CV02", "LIFT01", "CV03", "ASRS01"
    };

    // 设备 ID → 模拟器 映射
    private readonly Dictionary<string, DeviceSimulatorBase> _deviceMap = new();

    public SimulatorOrchestrator(
        VirtualPlant plant,
        ITaskScheduler scheduler,
        IEventBus signalBus,
        IStateCenter? stateCenter = null,
        ILogger<SimulatorOrchestrator>? logger = null)
    {
        _plant = plant;
        _scheduler = scheduler;
        _signalBus = signalBus;
        _stateCenter = stateCenter;
        _logger = logger;

        // 构建设备映射
        foreach (var cv in plant.Conveyors) _deviceMap[cv.DeviceId] = cv;
        foreach (var l in plant.Lifts) _deviceMap[l.DeviceId] = l;
        foreach (var a in plant.AsrsMachines) _deviceMap[a.DeviceId] = a;
        foreach (var r in plant.Robots) _deviceMap[r.DeviceId] = r;
    }

    /// <summary>
    /// 启动完整模拟闭环
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogInformation("SimulatorOrchestrator 启动 — 创建模拟执行闭环");

        // 并行运行三个核心循环
        var tasks = new[]
        {
            Task.Run(() => SignalReaderLoopAsync(ct), ct),
            Task.Run(() => TaskExecutorLoopAsync(ct), ct),
            Task.Run(() => MetricsLogLoopAsync(ct), ct),
        };

        await Task.WhenAny(tasks); // 任一退出则整体退出
        _running = false;
    }

    public void Stop() => _running = false;

    /// <summary>
    /// 循环 1: 读取模拟 PLC 信号 → 发布到 SignalBus → 进入真实管线
    /// </summary>
    private async Task SignalReaderLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("SignalReaderLoop 启动: 模拟信号 → SignalBus");

        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var signals = await _plant.SignalSource.ReadAsync(ct);
                foreach (var signal in signals)
                {
                    // 将模拟信号发布到 SignalBus，让真实管线处理
                    var wcsEvent = ConvertToEvent(signal);
                    await _signalBus.PublishAsync(wcsEvent, ct);

                    _logger?.LogDebug("SignalBus <- {SignalId} = {Value}", signal.SignalId, signal.Value);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SignalReaderLoop 异常");
            }

            await Task.Delay(50, ct); // 50ms 轮询间隔
        }
    }

    /// <summary>
    /// 循环 2: 出队任务 → 模拟运输执行 → 触发设备模拟器
    /// </summary>
    private async Task TaskExecutorLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("TaskExecutorLoop 启动: 出队任务 → 模拟运输");

        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var task = await _scheduler.DequeueAsync(ct);
                if (task == null)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                // 异步执行模拟运输（不阻塞出队循环）
                _ = SimulateTransportAsync(task, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "TaskExecutorLoop 异常");
            }
        }
    }

    /// <summary>
    /// 模拟一个任务的完整运输过程
    /// </summary>
    private async Task SimulateTransportAsync(TaskContext task, CancellationToken ct)
    {
        var palletId = task.Tags.TryGetValue("PalletId", out var p) ? p : task.TaskId;
        _logger?.LogInformation("🚚 模拟运输: {Pallet} ({Route})", palletId, task.RouteId);

        // 更新 StateCenter 状态
        _stateCenter?.UpdateTaskRuntime(task.TaskId, new()
        {
            TaskId = task.TaskId,
            Status = Wcs.Core.StateCenter.Models.TaskStatusEnum.Running,
            StartTime = DateTime.UtcNow
        });

        // 沿路径逐个触发设备模拟器
        foreach (var nodeId in DefaultRoute)
        {
            if (ct.IsCancellationRequested) break;

            // 查找该节点对应的设备模拟器
            if (_deviceMap.TryGetValue(nodeId, out var device))
            {
                _logger?.LogDebug("  → {Device} 开始运输 {Pallet}...", nodeId, palletId);
                await device.StartAsync(ct);
                _logger?.LogDebug("  → {Device} 完成运输 {Pallet}", nodeId, palletId);
            }
            else
            {
                // 非设备节点（如 RECV_DOCK），模拟耗时
                await Task.Delay(1000, ct);
            }
        }

        // 任务完成
        _stateCenter?.UpdateTaskRuntime(task.TaskId, new()
        {
            TaskId = task.TaskId,
            Status = Wcs.Core.StateCenter.Models.TaskStatusEnum.Completed,
            EndTime = DateTime.UtcNow
        });

        _logger?.LogInformation("✅ 运输完成: {Pallet} ({Route})", palletId, task.RouteId);
    }

    /// <summary>
    /// 循环 3: 定期输出指标日志（可观测性）
    /// </summary>
    private async Task MetricsLogLoopAsync(CancellationToken ct)
    {
        var lastGen = 0L;
        while (!ct.IsCancellationRequested && _running)
        {
            await Task.Delay(10000, ct);
            var gen = _plant.Generator.Generated;
            var tps = gen - lastGen;
            lastGen = gen;
            _logger?.LogInformation("📊 模拟指标: 已生成 {Total} 任务, 最近 10s TPS = {Tps}", gen, tps / 10);
        }
    }

    /// <summary>
    /// 将模拟信号转换为业务事件（简化版：统一用 DeviceStateChangedEvent）
    /// </summary>
    private static IEvent ConvertToEvent(SignalChangedEvent signal)
    {
        // 提取 DeviceId（信号名如 "CV01.Arrived" → "CV01"）
        var deviceId = signal.SignalId.Contains('.')
            ? signal.SignalId.Split('.')[0]
            : signal.SignalId;

        var status = signal.SignalId.EndsWith("Arrived") || signal.SignalId.EndsWith("Ready")
            ? "Running"
            : "Idle";

        return new DeviceStateChangedEvent
        {
            DeviceId = deviceId,
            NewStatus = Wcs.Core.StateCenter.Models.DeviceStatusEnum.Running,
            OldStatus = Wcs.Core.StateCenter.Models.DeviceStatusEnum.Idle
        };
    }
}
