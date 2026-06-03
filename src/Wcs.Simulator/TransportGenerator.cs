namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 运输生成器 — 模拟 WMS 下发任务，随机生成运输任务
/// 每秒生成 N 个随机运输任务，覆盖不同起终点组合。
/// </summary>
public class TransportGenerator
{
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<TransportGenerator>? _logger;
    private readonly Random _rng = new();
    private readonly List<string> _sourceNodes = new() { "RECV_DOCK_A", "RECV_DOCK_B", "RECV_DOCK_C" };
    private readonly List<string> _targetNodes = new() { "ASRS_01", "ASRS_02", "ASRS_03", "ASRS_04", "ASRS_05", "OUT_DOCK" };
    private int _palletCounter;
    private bool _running;
    private long _generated;

    public int TasksPerSecond { get; set; } = 1;
    public long Generated => Interlocked.Read(ref _generated);

    public TransportGenerator(ITaskScheduler scheduler, ILogger<TransportGenerator>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogInformation("====== TransportGenerator: {Tps} tasks/s ======", TasksPerSecond);
        var logged = 0L;

        while (!ct.IsCancellationRequested && _running)
        {
            for (int i = 0; i < TasksPerSecond; i++)
            {
                var task = CreateRandomTransport();
                await _scheduler.EnqueueAsync(task, ct);
                var total = Interlocked.Increment(ref _generated);
                logged++;

                // 每秒首条任务输出 Info 日志
                if (logged <= 3 || total <= 10 || total % 50 == 0)
                {
                    _logger?.LogInformation(
                        "[生成] 📦 #{Total}  Task={TaskId}  Pallet={Pallet}  Route={Route}  Device={Device}",
                        total,
                        task.TaskId,
                        task.Tags.TryGetValue("PalletId", out var p) ? p : "?",
                        task.RouteId,
                        task.DeviceId);
                }
            }

            if (_generated > 0 && _generated % 50 == 0)
                _logger?.LogInformation("[生成] 📊 已生成 {Total} 个运输任务", _generated);

            await Task.Delay(1000, ct);
        }
    }

    public void Stop() => _running = false;

    private TaskContext CreateRandomTransport()
    {
        var palletId = $"PALLET_{++_palletCounter:D6}";
        var source = _sourceNodes[_rng.Next(_sourceNodes.Count)];
        var target = _targetNodes[_rng.Next(_targetNodes.Count)];

        var task = new TaskContext
        {
            DeviceId = source,
            Priority = _rng.Next(1, 5),
            PriorityLevel = (TaskPriority)_rng.Next(1, 5),
            Category = TaskCategory.Production,
            RouteId = $"{source}->{target}",
            Tags =
            {
                ["PalletId"] = palletId,
                ["SourceNode"] = source,
                ["TargetNode"] = target,
                ["Simulated"] = "true"
            }
        };
        task.Parameters["PalletId"] = palletId;
        task.Parameters["FromNode"] = source;
        task.Parameters["ToNode"] = target;

        return task;
    }

    public void RegisterNode(string node, bool isSource)
    {
        if (isSource) _sourceNodes.Add(node);
        else _targetNodes.Add(node);
    }
}
