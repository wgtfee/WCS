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
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

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
                var anomalies = _cycles.GetAnomalies(10_000)
                    .OrderBy(static item => item.DetectedAtUtc)
                    .ToArray();
                var retainedIds = anomalies
                    .Select(static anomaly => anomaly.AnomalyId)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var anomaly in anomalies)
                {
                    if (!_seen.TryAdd(anomaly.AnomalyId, 0)) continue;
                    if (_sink.TryWrite(ToEvidence(anomaly))) continue;

                    // 通道暂时满时不丢失去重机会，下一个轮询周期重新尝试。
                    _seen.TryRemove(anomaly.AnomalyId, out _);
                }

                // 周期服务是有界集合；异常离开其保留窗口后同步释放去重状态。
                foreach (var anomalyId in _seen.Keys)
                {
                    if (retainedIds.Contains(anomalyId)) continue;
                    _seen.TryRemove(anomalyId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Host shutdown.
        }
    }

    private AnomalyEvidence ToEvidence(TransportCycleAnomalyRecord anomaly)
    {
        var observed = NormalizeObservedUtc(anomaly.DetectedAtUtc);
        return new AnomalyEvidence
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
            ObservedAtUtc = observed,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(_options.EvidenceRetentionSeconds),
            Score = NormalizeScore(anomaly),
            Confidence = 0,
            Severity = anomaly.Kind == TransportCycleAnomalyKind.InvalidSequence
                ? PlcAnomalySeverity.Error
                : PlcAnomalySeverity.Warning,
            Reason = anomaly.Reason,
            ContextJson = null
        };
    }

    private static DateTime NormalizeObservedUtc(DateTime value)
    {
        var now = DateTime.UtcNow;
        if (value == default) return now;
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc > now.AddMinutes(1) ? now : utc;
    }

    private static double NormalizeScore(TransportCycleAnomalyRecord anomaly)
    {
        if (anomaly.Kind == TransportCycleAnomalyKind.InvalidSequence) return 0.95;
        var deviation = Math.Max(0, anomaly.Deviation ?? 0);
        return Math.Clamp(0.55 + 0.45 * (1.0 - Math.Exp(-deviation / 6.0)), 0, 1);
    }
}
