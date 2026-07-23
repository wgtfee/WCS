namespace Wcs.Host.BackgroundServices;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>订阅真实 RawSignalEvent 链路，并周期检查持续时间异常。</summary>
public sealed class PlcAnomalyDetectionService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly IPlcAnomalyEngine _engine;
    private readonly PlcAnomalyOptions _options;
    private readonly ILogger<PlcAnomalyDetectionService> _logger;

    public PlcAnomalyDetectionService(
        IEventBus eventBus,
        IPlcAnomalyEngine engine,
        PlcAnomalyOptions options,
        ILogger<PlcAnomalyDetectionService> logger)
    {
        _eventBus = eventBus;
        _engine = engine;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PLC anomaly detection disabled");
            return;
        }

        _eventBus.Subscribe<RawSignalEvent>(async (evt, ct) =>
        {
            var sample = PlcAnomalySampleFactory.FromRawSignal(evt);
            await _engine.ProcessAsync(sample, ct);
        });

        _logger.LogInformation(
            "PLC anomaly engine started: Rules={RuleCount}, Window={WindowSize}, MinimumSamples={MinimumSamples}",
            _options.Rules.Count,
            _options.WindowSize,
            _options.MinimumSamples);

        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(Math.Max(100, _options.DurationSweepIntervalMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await _engine.SweepAsync(DateTime.UtcNow, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Host shutdown.
        }
    }
}
