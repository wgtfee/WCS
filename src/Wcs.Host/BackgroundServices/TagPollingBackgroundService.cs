namespace Wcs.Host.BackgroundServices;

using Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签轮询后台服务 — 包装 TagPollingService 为 BackgroundService
/// 对标 S7PollingBackgroundService 的模式
/// </summary>
public class TagPollingBackgroundService : BackgroundService
{
    private readonly TagPollingService _pollingService;
    private readonly ILogger<TagPollingBackgroundService> _logger;

    public TagPollingBackgroundService(
        TagPollingService pollingService,
        ILogger<TagPollingBackgroundService> logger)
    {
        _pollingService = pollingService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("标签轮询后台服务启动");
        _pollingService.Start();
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pollingService.Stop();
        await base.StopAsync(cancellationToken);
    }
}
