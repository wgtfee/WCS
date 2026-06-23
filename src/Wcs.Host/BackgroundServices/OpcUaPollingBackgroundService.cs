namespace Wcs.Host.BackgroundServices;

using Wcs.Core.PlcSubsystem.OpcUa;

public class OpcUaPollingBackgroundService : BackgroundService
{
    private readonly OpcUaPollingService _pollingService;
    private readonly ILogger<OpcUaPollingBackgroundService> _logger;

    public OpcUaPollingBackgroundService(
        OpcUaPollingService pollingService,
        ILogger<OpcUaPollingBackgroundService> logger)
    {
        _pollingService = pollingService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OPC UA 标签轮询后台服务启动");
        _pollingService.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollingService.Stop();
        await base.StopAsync(cancellationToken);
    }
}
