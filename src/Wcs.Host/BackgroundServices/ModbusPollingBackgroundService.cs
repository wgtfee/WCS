namespace Wcs.Host.BackgroundServices;

using Wcs.Core.PlcSubsystem.Modbus;

public class ModbusPollingBackgroundService : BackgroundService
{
    private readonly ModbusPollingService _pollingService;
    private readonly ILogger<ModbusPollingBackgroundService> _logger;

    public ModbusPollingBackgroundService(
        ModbusPollingService pollingService,
        ILogger<ModbusPollingBackgroundService> logger)
    {
        _pollingService = pollingService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Modbus 标签轮询后台服务启动");
        _pollingService.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollingService.Stop();
        await base.StopAsync(cancellationToken);
    }
}
