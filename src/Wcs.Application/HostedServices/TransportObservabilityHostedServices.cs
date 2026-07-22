namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

public sealed class TransportObservabilityInitializationHostedService : IHostedService
{
    private readonly ITransportConsistencyInspectionService _consistency;
    private readonly ITransportObservabilityService _observability;
    private readonly ILogger<TransportObservabilityInitializationHostedService> _logger;

    public TransportObservabilityInitializationHostedService(
        ITransportConsistencyInspectionService consistency,
        ITransportObservabilityService observability,
        ILogger<TransportObservabilityInitializationHostedService> logger)
    {
        _consistency = consistency;
        _observability = observability;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _consistency.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _observability.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EMS/RGV 可观测性历史状态加载完成");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TransportConsistencyInspectionHostedService : BackgroundService
{
    private readonly ITransportConsistencyInspectionService _service;
    private readonly TransportObservabilityOptions _options;
    private readonly ILogger<TransportConsistencyInspectionHostedService> _logger;

    public TransportConsistencyInspectionHostedService(
        ITransportConsistencyInspectionService service,
        TransportObservabilityOptions options,
        ILogger<TransportConsistencyInspectionHostedService> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var interval = TimeSpan.FromSeconds(
            Math.Clamp(_options.ConsistencyInspectionIntervalSeconds, 5, 3600));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
                await InspectAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task InspectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _service.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!report.IsConsistent)
            {
                _logger.LogWarning(
                    "EMS/RGV 三方一致性巡检发现差异：Critical={Critical}, Error={Error}, Warning={Warning}",
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
            _logger.LogWarning(ex, "EMS/RGV 三方一致性巡检周期失败，Host 继续运行");
        }
    }
}

public sealed class TransportHealthEvaluationHostedService : BackgroundService
{
    private readonly ITransportObservabilityService _service;
    private readonly TransportObservabilityOptions _options;
    private readonly ILogger<TransportHealthEvaluationHostedService> _logger;

    public TransportHealthEvaluationHostedService(
        ITransportObservabilityService service,
        TransportObservabilityOptions options,
        ILogger<TransportHealthEvaluationHostedService> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        await EvaluateAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(_options.HealthEvaluationIntervalSeconds, 5, 3600)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var health = await _service.EvaluateHealthAsync(cancellationToken).ConfigureAwait(false);
            if (health.State != TransportHealthState.Healthy)
            {
                _logger.LogWarning(
                    "EMS/RGV 运输系统健康状态 {State}，评分 {Score}",
                    health.State,
                    health.Score);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EMS/RGV 健康评分周期失败，Host 继续运行");
        }
    }
}
