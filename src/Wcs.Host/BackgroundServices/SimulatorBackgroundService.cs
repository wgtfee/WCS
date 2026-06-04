namespace Wcs.Host.BackgroundServices;

using Wcs.Simulator.PlcSimulatorEngine;

public class SimulatorBackgroundService : BackgroundService
{
    private readonly SimulatedPlcPollingService _simulatedService;
    private readonly ILogger<SimulatorBackgroundService> _logger;

    public SimulatorBackgroundService(
        SimulatedPlcPollingService simulatedService,
        ILogger<SimulatorBackgroundService> logger)
    {
        _simulatedService = simulatedService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("  模拟 PLC 轮询服务启动");
        _logger.LogInformation("  3 PLC × 3 DB = 9 个模拟块");
        _logger.LogInformation("  18 个真实验证器全部就位");
        _logger.LogInformation("========================================");

        _simulatedService.RegisterDefaultValidators();
        _simulatedService.Start();

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _simulatedService.Stop();
        await base.StopAsync(cancellationToken);
    }
}
