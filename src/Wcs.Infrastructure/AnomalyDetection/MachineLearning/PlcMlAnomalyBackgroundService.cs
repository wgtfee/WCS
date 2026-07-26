namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMlAnomalyBackgroundService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly IPlcMlAnomalyEngine _engine;
    private readonly IPlcMlContextPeerRuntime _contextPeerRuntime;
    private readonly PlcMlAnomalyOptions _options;
    private readonly ILogger<PlcMlAnomalyBackgroundService> _logger;

    public PlcMlAnomalyBackgroundService(
        IEventBus eventBus,
        IPlcMlAnomalyEngine engine,
        IPlcMlContextPeerRuntime contextPeerRuntime,
        PlcMlAnomalyOptions options,
        ILogger<PlcMlAnomalyBackgroundService> logger)
    {
        _eventBus = eventBus;
        _engine = engine;
        _contextPeerRuntime = contextPeerRuntime;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PLC machine-learning anomaly detection disabled");
            return;
        }

        await _engine.InitializeAsync(stoppingToken);
        _eventBus.Subscribe<RawSignalEvent>(async (evt, ct) =>
        {
            var sample = PlcAnomalySampleFactory.FromRawSignal(evt);
            await _engine.ProcessAsync(sample, ct);
            await _contextPeerRuntime.ProcessAsync(sample, ct);
        });

        _logger.LogInformation(
            "PLC ML and contextual-peer anomaly engines started: Profiles={ProfileCount}, ModelDirectory={ModelDirectory}, TrainingDirectory={TrainingDirectory}",
            _options.Profiles.Count,
            _options.ModelDirectory,
            _options.TrainingDirectory);

        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(Math.Max(100, _options.MaintenanceIntervalMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var utcNow = DateTime.UtcNow;
                await _engine.MaintenanceAsync(utcNow, stoppingToken);
                await _contextPeerRuntime.MaintenanceAsync(utcNow, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Host shutdown.
        }
    }
}
