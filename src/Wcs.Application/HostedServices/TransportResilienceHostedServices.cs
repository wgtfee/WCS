namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

public sealed class TransportResilienceInitializationHostedService : IHostedService
{
    private readonly ITransportResilienceService _service;
    private readonly ILogger<TransportResilienceInitializationHostedService> _logger;

    public TransportResilienceInitializationHostedService(
        ITransportResilienceService service,
        ILogger<TransportResilienceInitializationHostedService> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _service.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EMS/RGV 生产韧性历史状态加载完成");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TransportReadinessHostedService : BackgroundService
{
    private readonly ITransportResilienceService _service;
    private readonly TransportResilienceOptions _options;
    private readonly ILogger<TransportReadinessHostedService> _logger;

    public TransportReadinessHostedService(
        ITransportResilienceService service,
        TransportResilienceOptions options,
        ILogger<TransportReadinessHostedService> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(_options.PreflightIntervalSeconds, 10, 3600)));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _service.RunPreflightAsync(cancellationToken).ConfigureAwait(false);
            if (!report.IsReady)
            {
                _logger.LogWarning(
                    "EMS/RGV 生产就绪检查未通过：Critical={Critical}, Error={Error}, Warning={Warning}",
                    report.CriticalCount,
                    report.ErrorCount,
                    report.WarningCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EMS/RGV 生产就绪检查失败，Host 继续运行");
        }
    }
}

public sealed class TransportAutomaticBackupHostedService : BackgroundService
{
    private readonly ITransportResilienceService _service;
    private readonly TransportResilienceOptions _options;
    private readonly ILogger<TransportAutomaticBackupHostedService> _logger;

    public TransportAutomaticBackupHostedService(
        ITransportResilienceService service,
        TransportResilienceOptions options,
        ILogger<TransportAutomaticBackupHostedService> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.AutomaticBackupEnabled)
            return;

        var interval = TimeSpan.FromMinutes(Math.Clamp(_options.BackupIntervalMinutes, 1, 1440));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
                var backup = await _service.CreateBackupAsync(
                    $"automatic-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    "周期性生产逻辑备份",
                    "system",
                    stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "EMS/RGV 自动逻辑备份完成：BackupId={BackupId}, Size={SizeBytes}, Ready={Ready}",
                    backup.BackupId,
                    backup.SizeBytes,
                    backup.PreflightReady);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 自动逻辑备份失败，Host 继续运行");
            }
        }
    }
}
