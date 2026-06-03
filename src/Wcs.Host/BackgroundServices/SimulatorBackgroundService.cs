namespace Wcs.Host.BackgroundServices;

using Wcs.Simulator;

/// <summary>
/// 虚拟工厂后台服务 — 开发/测试时替代真实 PLC 轮询服务
///
/// 启动后自动运行 VirtualPlant 的快速测试场景，
/// 使 WCS Core 在无真实 PLC/设备的环境下完整运行。
/// </summary>
public class SimulatorBackgroundService : BackgroundService
{
    private readonly VirtualPlant _plant;
    private readonly ILogger<SimulatorBackgroundService> _logger;

    public SimulatorBackgroundService(VirtualPlant plant, ILogger<SimulatorBackgroundService> logger)
    {
        _plant = plant;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SimulatorBackgroundService 启动 — 虚拟工厂开始运行");

        // 持续生成模拟运输任务（每秒 2 个）
        _plant.Generator.TasksPerSecond = 2;

        await _plant.Generator.StartAsync(stoppingToken);
    }
}
