namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

public sealed class TransportPlcSignalMapHostedService : IHostedService
{
    private readonly ITransportPlcSignalMapService _maps;
    private readonly ILogger<TransportPlcSignalMapHostedService> _logger;

    public TransportPlcSignalMapHostedService(
        ITransportPlcSignalMapService maps,
        ILogger<TransportPlcSignalMapHostedService> logger)
    {
        _maps = maps;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _maps.LoadAndApplyAsync(cancellationToken).ConfigureAwait(false);
        var count = (await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count;
        _logger.LogInformation("EMS/RGV PLC 点位映射已加载，共 {Count} 辆车", count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TransportDriverReconciliationHostedService : IHostedService
{
    private readonly ITransportDriverSynchronizationService _synchronization;
    private readonly ILogger<TransportDriverReconciliationHostedService> _logger;

    public TransportDriverReconciliationHostedService(
        ITransportDriverSynchronizationService synchronization,
        ILogger<TransportDriverReconciliationHostedService> logger)
    {
        _synchronization = synchronization;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var report = await _synchronization.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "EMS/RGV 驱动启动对账完成：一致 {InSyncCount}，需人工确认 {ManualCount}，总计 {Total}",
            report.InSyncCount,
            report.ManualConfirmationCount,
            report.Items.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TransportDriverPollingHostedService : BackgroundService
{
    private readonly ITransportDriverSynchronizationService _synchronization;
    private readonly ILogger<TransportDriverPollingHostedService> _logger;

    public TransportDriverPollingHostedService(
        ITransportDriverSynchronizationService synchronization,
        ILogger<TransportDriverPollingHostedService> logger)
    {
        _synchronization = synchronization;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await _synchronization.PollAllAsync(stoppingToken).ConfigureAwait(false);
                foreach (var failed in report.Items.Where(x => x.Decision == TransportDriverSyncDecision.Failed))
                    _logger.LogWarning("车辆 {VehicleId} PLC 状态同步失败：{Message}", failed.VehicleId, failed.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV PLC 状态轮询失败，本周期已跳过");
            }

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
}
