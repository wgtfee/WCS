namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcAnomalyFusionBridgeService : BackgroundService
{
    private readonly AnomalyFusionOptions _options;
    private readonly IEventBus _eventBus;
    private readonly IAnomalyEvidenceSink _sink;
    private readonly ILogger<PlcAnomalyFusionBridgeService> _logger;

    public PlcAnomalyFusionBridgeService(
        AnomalyFusionOptions options,
        IEventBus eventBus,
        IAnomalyEvidenceSink sink,
        ILogger<PlcAnomalyFusionBridgeService> logger)
    {
        _options = options;
        _eventBus = eventBus;
        _sink = sink;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PLC anomaly fusion bridge disabled");
            return;
        }

        _eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            _sink.TryWrite(ToEvidence(evt.Anomaly, AnomalyEvidenceState.Active));
            return Task.CompletedTask;
        });

        _eventBus.Subscribe<PlcAnomalyRecoveredEvent>((evt, _) =>
        {
            _sink.TryWrite(ToEvidence(evt.Anomaly, AnomalyEvidenceState.Recovered));
            return Task.CompletedTask;
        });

        _logger.LogInformation("PLC anomaly fusion bridge subscribed");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static AnomalyEvidence ToEvidence(PlcAnomalyRecord anomaly, AnomalyEvidenceState state)
    {
        var observed = state == AnomalyEvidenceState.Recovered
            ? anomaly.EndTimeUtc ?? anomaly.LastSeenUtc
            : anomaly.LastSeenUtc;

        return new AnomalyEvidence
        {
            EvidenceId = $"PLC|{anomaly.AnomalyId}",
            Source = anomaly.Type.ToString(),
            AssetId = string.IsNullOrWhiteSpace(anomaly.DeviceId) ? anomaly.PlcName : anomaly.DeviceId,
            RelatedEntityId = anomaly.RuleId,
            Category = anomaly.Type.ToString(),
            State = state,
            ObservedAtUtc = observed == default ? DateTime.UtcNow : observed,
            Score = NormalizeScore(anomaly),
            Confidence = 0,
            Severity = anomaly.Severity,
            Reason = anomaly.Reason,
            ContextJson = anomaly.ContextJson
        };
    }

    private static double NormalizeScore(PlcAnomalyRecord anomaly)
    {
        var severityFloor = anomaly.Severity switch
        {
            PlcAnomalySeverity.Observe => 0.25,
            PlcAnomalySeverity.Warning => 0.60,
            PlcAnomalySeverity.Error => 0.82,
            PlcAnomalySeverity.Critical => 0.96,
            _ => 0.50
        };

        if (!double.IsFinite(anomaly.Score) || anomaly.Score <= 0) return severityFloor;
        if (anomaly.Score <= 1) return Math.Max(severityFloor, anomaly.Score);

        return Math.Clamp(Math.Max(severityFloor, 1.0 - Math.Exp(-anomaly.Score / 6.0)), 0, 1);
    }
}
