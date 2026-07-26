namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.TransportScheduling;

public sealed class TransportCycleFusionBridgeService : BackgroundService
{
    private readonly AnomalyFusionOptions _options;
    private readonly ITransportCycleAnalysisService _cycles;
    private readonly IAnomalyEvidenceSink _sink;
    private readonly ILogger<TransportCycleFusionBridgeService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    public TransportCycleFusionBridgeService(
        AnomalyFusionOptions options,
        ITransportCycleAnalysisService cycles,
        IAnomalyEvidenceSink sink,
        ILogger<TransportCycleFusionBridgeService> logger)
    {
        _options = options;
        _cycles = cycles;
        _sink = sink;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Transport cycle fusion bridge disabled");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _logger.LogInformation("Transport cycle fusion bridge started");
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                foreach (var anomaly in _cycles.GetAnomalies(10_000).OrderBy(static item => item.DetectedAtUtc))
                {
                    if (!_seen.TryAdd(anomaly.AnomalyId, now)) continue;
                    _sink.TryWrite(ToEvidence(anomaly));
                }

                var cutoff = now.AddSeconds(-Math.Max(60, _options.EvidenceRetentionSeconds * 2));
                foreach (var pair in _seen)
                {
                    if (pair.Value >= cutoff) continue;
                    ((ICollection<KeyValuePair<string, DateTime>>)_seen).Remove(pair);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Host shutdown.
        }
    }

    private static AnomalyEvidence ToEvidence(TransportCycleAnomalyRecord anomaly) => new()
    {
        EvidenceId = $"CYCLE|{anomaly.AnomalyId}",
        Source = anomaly.Kind switch
        {
            TransportCycleAnomalyKind.InvalidSequence => AnomalyEvidenceSources.CycleSequence,
            TransportCycleAnomalyKind.PhaseDuration => AnomalyEvidenceSources.CyclePhaseDuration,
            TransportCycleAnomalyKind.TotalDuration => AnomalyEvidenceSources.CycleTotalDuration,
            _ => $"CYCLE_{anomaly.Kind.ToString().ToUpperInvariant()}"
        },
        AssetId = anomaly.VehicleId,
        RelatedEntityId = anomaly.RequestId,
        Category = anomaly.Kind.ToString(),
        State = AnomalyEvidenceState.Active,
        ObservedAtUtc = anomaly.DetectedAtUtc,
        Score = NormalizeScore(anomaly),
        Confidence = 0,
        Severity = anomaly.Kind == TransportCycleAnomalyKind.InvalidSequence
            ? PlcAnomalySeverity.Error
            : PlcAnomalySeverity.Warning,
        Reason = anomaly.Reason,
        ContextJson = null
    };

    private static double NormalizeScore(TransportCycleAnomalyRecord anomaly)
    {
        if (anomaly.Kind == TransportCycleAnomalyKind.InvalidSequence) return 0.95;
        var deviation = Math.Max(0, anomaly.Deviation ?? 0);
        return Math.Clamp(0.55 + 0.45 * (1.0 - Math.Exp(-deviation / 6.0)), 0, 1);
    }
}
