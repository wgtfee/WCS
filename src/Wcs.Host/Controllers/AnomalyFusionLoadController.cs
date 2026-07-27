namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 仅 LoadTest 环境启用。通过真实 EventBus -> PLC Bridge -> Evidence Channel ->
/// Fusion Engine 链路验证多来源升级和恢复，不直接写入 Fusion 状态。
/// </summary>
[ApiController]
[Route("api/anomaly/fusion/load")]
public sealed class AnomalyFusionLoadController : ControllerBase
{
    private readonly IEventBus _eventBus;
    private readonly IAnomalyFusionEngine _engine;
    private readonly IAnomalyEvidenceIngressStatus _ingress;
    private readonly IHostEnvironment _environment;

    public AnomalyFusionLoadController(
        IEventBus eventBus,
        IAnomalyFusionEngine engine,
        IAnomalyEvidenceIngressStatus ingress,
        IHostEnvironment environment)
    {
        _eventBus = eventBus;
        _engine = engine;
        _ingress = ingress;
        _environment = environment;
    }

    [HttpPost("plc-lifecycle")]
    public async Task<ActionResult> RunPlcLifecycle(
        [FromBody] AnomalyFusionPlcLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();

        var assetId = string.IsNullOrWhiteSpace(request.AssetId)
            ? "FUSION-PLC-E2E"
            : request.AssetId.Trim();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 3, 30));
        var runId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var beforeFusion = _engine.GetStatus();
        var beforeIngress = _ingress.GetStatus();

        var threshold = CreateRecord(
            anomalyId: $"FUSION-THRESHOLD-{runId}",
            assetId,
            PlcAnomalyType.Threshold,
            PlcAnomalySeverity.Critical,
            score: 0.99,
            now);
        var machineLearning = CreateRecord(
            anomalyId: $"FUSION-ML-{runId}",
            assetId,
            PlcAnomalyType.MachineLearning,
            PlcAnomalySeverity.Error,
            score: 0.96,
            now.AddMilliseconds(1));

        await _eventBus.PublishAsync(new PlcAnomalyDetectedEvent { Anomaly = threshold }, cancellationToken);
        await _eventBus.PublishAsync(new PlcAnomalyDetectedEvent { Anomaly = machineLearning }, cancellationToken);

        var alarm = await WaitForSnapshotAsync(
            assetId,
            snapshot => snapshot.Status == FusedHealthStatus.Alarm &&
                        snapshot.IndependentSourceCount == 2 &&
                        _ingress.GetStatus().Pending == 0,
            timeout,
            cancellationToken);
        if (alarm is null)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                stage = "alarm",
                assetId,
                fusion = _engine.GetStatus(),
                ingress = _ingress.GetStatus(),
                asset = _engine.GetAsset(assetId)
            });
        }

        var recoveredUtc = DateTime.UtcNow;
        await _eventBus.PublishAsync(
            new PlcAnomalyRecoveredEvent { Anomaly = threshold.Recover(recoveredUtc) },
            cancellationToken);
        await _eventBus.PublishAsync(
            new PlcAnomalyRecoveredEvent { Anomaly = machineLearning.Recover(recoveredUtc.AddMilliseconds(1)) },
            cancellationToken);

        var normal = await WaitForSnapshotAsync(
            assetId,
            snapshot => snapshot.Status == FusedHealthStatus.Normal &&
                        snapshot.IndependentSourceCount == 0 &&
                        _ingress.GetStatus().Pending == 0,
            timeout,
            cancellationToken);
        if (normal is null)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                stage = "recovery",
                assetId,
                fusion = _engine.GetStatus(),
                ingress = _ingress.GetStatus(),
                asset = _engine.GetAsset(assetId)
            });
        }

        var afterFusion = _engine.GetStatus();
        var afterIngress = _ingress.GetStatus();
        return Ok(new
        {
            pipeline = "PlcAnomalyDetectedEvent/RecoveredEvent->EventBus->PlcAnomalyFusionBridge->EvidenceChannel->FusionEngine->ReadOnlyAPI",
            assetId,
            alarm = SnapshotResult(alarm),
            recovery = SnapshotResult(normal),
            evidenceAcceptedDelta = afterFusion.EvidenceAccepted - beforeFusion.EvidenceAccepted,
            evidenceRecoveredDelta = afterFusion.EvidenceRecovered - beforeFusion.EvidenceRecovered,
            alarmTransitionDelta = afterFusion.AlarmTransitions - beforeFusion.AlarmTransitions,
            recoveryTransitionDelta = afterFusion.RecoveryTransitions - beforeFusion.RecoveryTransitions,
            ingressWrittenDelta = afterIngress.Written - beforeIngress.Written,
            ingressReadDelta = afterIngress.Read - beforeIngress.Read,
            ingressDroppedDelta = afterIngress.Dropped - beforeIngress.Dropped,
            ingressPending = afterIngress.Pending,
            fusion = afterFusion,
            ingress = afterIngress
        });
    }

    private async Task<FusedHealthSnapshot?> WaitForSnapshotAsync(
        string assetId,
        Func<FusedHealthSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _engine.GetAsset(assetId);
            if (snapshot is not null && predicate(snapshot)) return snapshot;
            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    private static object SnapshotResult(FusedHealthSnapshot snapshot) => new
    {
        snapshot.AssetId,
        status = snapshot.Status.ToString(),
        snapshot.Score,
        snapshot.IndependentSourceCount,
        sources = snapshot.Evidence.Select(static item => item.Source).OrderBy(static item => item).ToArray(),
        snapshot.LastEvaluatedAtUtc
    };

    private static PlcAnomalyRecord CreateRecord(
        string anomalyId,
        string assetId,
        PlcAnomalyType type,
        PlcAnomalySeverity severity,
        double score,
        DateTime observedAtUtc) => new()
    {
        AnomalyId = anomalyId,
        AnomalyKey = $"{assetId}|{type}|{anomalyId}",
        AlarmCode = $"FUSION_{type.ToString().ToUpperInvariant()}",
        RuleId = $"FUSION-E2E-{type}",
        Type = type,
        Severity = severity,
        Status = PlcAnomalyLifecycleStatus.Active,
        PlcName = "FUSION-E2E-PLC",
        DbBlock = 990,
        DeviceId = assetId,
        SignalName = type == PlcAnomalyType.Threshold ? "Current" : "MlScore",
        DetectorName = type.ToString(),
        ModelVersion = type == PlcAnomalyType.MachineLearning ? "fusion-e2e-model" : "rule-v1",
        Score = score,
        StartTimeUtc = observedAtUtc,
        LastSeenUtc = observedAtUtc,
        Reason = $"fusion bridge e2e {type}",
        RaiseAlarm = false,
        ContextJson = "{\"source\":\"fusion-host-e2e\"}"
    };
}

public sealed class AnomalyFusionPlcLoadRequest
{
    public string AssetId { get; set; } = "FUSION-PLC-E2E";
    public int TimeoutSeconds { get; set; } = 15;
}
