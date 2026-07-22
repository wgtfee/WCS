namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

/// <summary>按顺序恢复生产参数、站点、单轨区段和趋势历史。</summary>
public sealed class TransportProductionConfigurationHostedService : IHostedService
{
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportProductionTrendService _trends;
    private readonly ILogger<TransportProductionConfigurationHostedService> _logger;

    public TransportProductionConfigurationHostedService(
        ITransportProductionTuningService tuning,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportProductionTrendService trends,
        ILogger<TransportProductionConfigurationHostedService> logger)
    {
        _tuning = tuning;
        _stations = stations;
        _singleTrack = singleTrack;
        _trends = trends;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _tuning.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _stations.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _singleTrack.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _trends.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EMS/RGV 第九阶段生产调度参数和运行历史已恢复");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>持续执行生产任务竞争；单次失败不会终止 Host。</summary>
public sealed class TransportProductionDispatchHostedService : BackgroundService
{
    private readonly ITransportProductionDispatchService _production;
    private readonly ILogger<TransportProductionDispatchHostedService> _logger;

    public TransportProductionDispatchHostedService(
        ITransportProductionDispatchService production,
        ILogger<TransportProductionDispatchHostedService> logger)
    {
        _production = production;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _production.DispatchCycleAsync(stoppingToken).ConfigureAwait(false);
                if (result.AssignedCount > 0)
                {
                    _logger.LogInformation(
                        "EMS/RGV 生产派单周期完成，竞争 {ConsideredCount}，成功 {AssignedCount}",
                        result.ConsideredCount,
                        result.AssignedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 生产派单周期失败，本周期已跳过");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public sealed class TransportProductionTrendHostedService : BackgroundService
{
    private readonly ITransportProductionTrendService _trends;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ILogger<TransportProductionTrendHostedService> _logger;

    public TransportProductionTrendHostedService(
        ITransportProductionTrendService trends,
        ITransportProductionTuningService tuning,
        ILogger<TransportProductionTrendHostedService> logger)
    {
        _trends = trends;
        _tuning = tuning;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _trends.CaptureAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 生产趋势采集失败，本周期已跳过");
            }

            try
            {
                var seconds = Math.Clamp(_tuning.Current.TrendCaptureIntervalSeconds, 5, 3600);
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public sealed class TransportFaultTakeoverHostedService : BackgroundService
{
    private readonly ITransportFaultTakeoverService _takeover;
    private readonly ILogger<TransportFaultTakeoverHostedService> _logger;

    public TransportFaultTakeoverHostedService(
        ITransportFaultTakeoverService takeover,
        ILogger<TransportFaultTakeoverHostedService> logger)
    {
        _takeover = takeover;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await _takeover.EvaluateAsync(stoppingToken).ConfigureAwait(false);
                var actionable = report.Items.Count(x => x.Decision is
                    TransportFaultTakeoverDecision.Reassigned or
                    TransportFaultTakeoverDecision.ManualRecoveryRequired or
                    TransportFaultTakeoverDecision.WaitingForPhysicalClearance);
                if (actionable > 0)
                    _logger.LogWarning("EMS/RGV 故障接管发现 {ActionableCount} 项需要关注", actionable);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 故障接管评估失败，本周期已跳过");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
