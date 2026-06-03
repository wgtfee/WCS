namespace Wcs.Host.BackgroundServices;

using Wcs.Simulator;

/// <summary>
/// 虚拟工厂后台服务 — 开发/测试时替代真实 PLC 轮询服务
///
/// 启动后自动运行完整模拟闭环：
/// TransportGenerator → TaskScheduler → SimulatorOrchestrator
///   → DeviceSimulator → SimulatorSignalSource → SignalBus → 真实管线
/// </summary>
public class SimulatorBackgroundService : BackgroundService
{
    private readonly VirtualPlant _plant;
    private readonly SimulatorOrchestrator _orchestrator;
    private readonly ILogger<SimulatorBackgroundService> _logger;

    public SimulatorBackgroundService(
        VirtualPlant plant,
        SimulatorOrchestrator orchestrator,
        ILogger<SimulatorBackgroundService> logger)
    {
        _plant = plant;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("  Simulator Background Service 启动");
        _logger.LogInformation("  设备数: {DeviceCount}", _plant.DeviceCount);
        _logger.LogInformation("========================================");

        // 模拟闭环三线程并行运行
        _plant.Generator.TasksPerSecond = 2;

        var tasks = new[]
        {
            // 线程1: 运输生成器（模拟 WMS 下发任务）
            _plant.Generator.StartAsync(stoppingToken),

            // 线程2: 模拟编排器（信号桥接 + 任务执行 + 指标日志）
            _orchestrator.RunAsync(stoppingToken),
        };

        await Task.WhenAll(tasks);
    }
}
