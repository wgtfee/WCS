namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 运输生成器 — 模拟 WMS 下发任务，随机生成运输任务
///
/// 用于压力测试和长时间稳定性测试。
/// 每秒生成 N 个随机运输任务，覆盖不同起终点组合。
/// </summary>
public class TransportGenerator
{
    private readonly ITaskScheduler _scheduler;
    private readonly ILogger<TransportGenerator>? _logger;
    private readonly Random _rng = new();
    private readonly List<string> _sourceNodes = new() { "RECV_DOCK_A", "RECV_DOCK_B", "RECV_DOCK_C" };
    private readonly List<string> _targetNodes = new() { "ASRS_01", "ASRS_02", "ASRS_03", "ASRS_04", "ASRS_05", "OUT_DOCK" };
    private readonly List<string> _palletPool = new();
    private int _palletCounter;
    private bool _running;
    private long _generated;

    /// <summary>每秒生成任务数</summary>
    public int TasksPerSecond { get; set; } = 1;

    /// <summary>已生成任务总数</summary>
    public long Generated => Interlocked.Read(ref _generated);

    public TransportGenerator(ITaskScheduler scheduler, ILogger<TransportGenerator>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger;
    }

    /// <summary>
    /// 启动生成器
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogInformation("TransportGenerator started: {Tps} tasks/s", TasksPerSecond);

        while (!ct.IsCancellationRequested && _running)
        {
            for (int i = 0; i < TasksPerSecond; i++)
            {
                var task = CreateRandomTransport();
                await _scheduler.EnqueueAsync(task, ct);
                Interlocked.Increment(ref _generated);
            }
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>
    /// 停止生成器
    /// </summary>
    public void Stop() => _running = false;

    /// <summary>
    /// 创建随机运输任务
    /// </summary>
    private TaskContext CreateRandomTransport()
    {
        var palletId = $"PALLET_{++_palletCounter:D6}";
        _palletPool.Add(palletId);

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

    /// <summary>
    /// 注册自定义节点（用于扩展运输拓扑）
    /// </summary>
    public void RegisterNode(string node, bool isSource)
    {
        if (isSource) _sourceNodes.Add(node);
        else _targetNodes.Add(node);
    }
}
