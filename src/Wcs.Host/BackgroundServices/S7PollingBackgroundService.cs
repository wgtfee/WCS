namespace Wcs.Host.BackgroundServices;

using Wcs.Core.PlcSubsystem.S7;

/// <summary>
/// S7 轮询后台服务 — 启动时自动读取 PlcStructRegistry 中所有 PLC 的轮询
/// </summary>
public class S7PollingBackgroundService : BackgroundService
{
    private readonly S7PollingService _pollingService;
    private readonly ILogger<S7PollingBackgroundService> _logger;

    public S7PollingBackgroundService(S7PollingService pollingService, ILogger<S7PollingBackgroundService> logger)
    {
        _pollingService = pollingService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("S7 轮询后台服务启动");
        _pollingService.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollingService.Stop();
        await base.StopAsync(cancellationToken);
    }
}
