namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.Fusion;

public sealed class AnomalyFusionBackgroundService : BackgroundService
{
    private readonly AnomalyFusionOptions _options;
    private readonly AnomalyEvidenceChannel _channel;
    private readonly IAnomalyFusionEngine _engine;
    private readonly ILogger<AnomalyFusionBackgroundService> _logger;

    public AnomalyFusionBackgroundService(
        AnomalyFusionOptions options,
        AnomalyEvidenceChannel channel,
        IAnomalyFusionEngine engine,
        ILogger<AnomalyFusionBackgroundService> logger)
    {
        _options = options;
        _channel = channel;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Anomaly evidence fusion disabled");
            return;
        }

        _logger.LogInformation(
            "Anomaly evidence fusion started: Capacity={Capacity}, Warning={Warning}, Alarm={Alarm}, MinAlarmSources={MinSources}",
            _options.ChannelCapacity,
            _options.WarningThreshold,
            _options.AlarmThreshold,
            _options.MinimumIndependentSourcesForAlarm);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var maintenance = RunMaintenanceAsync(timer, stoppingToken);
        try
        {
            await foreach (var evidence in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                _engine.Process(evidence);
                _channel.RecordRead();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Host shutdown.
        }
        finally
        {
            try { await maintenance; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunMaintenanceAsync(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
            _engine.Maintenance(DateTime.UtcNow);
    }
}
