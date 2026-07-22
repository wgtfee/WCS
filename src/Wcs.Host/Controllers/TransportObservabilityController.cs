namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/observability")]
public sealed class TransportObservabilityController : ControllerBase
{
    private readonly ITransportObservabilityService _observability;
    private readonly ITransportTelemetryService _telemetry;
    private readonly ITransportConsistencyInspectionService _consistency;
    private readonly ITransportConfigurationSnapshotService _snapshots;
    private readonly ITransportOperationGovernanceService _governance;

    public TransportObservabilityController(
        ITransportObservabilityService observability,
        ITransportTelemetryService telemetry,
        ITransportConsistencyInspectionService consistency,
        ITransportConfigurationSnapshotService snapshots,
        ITransportOperationGovernanceService governance)
    {
        _observability = observability;
        _telemetry = telemetry;
        _consistency = consistency;
        _snapshots = snapshots;
        _governance = governance;
    }

    [HttpGet("summary")]
    public ActionResult<TransportObservabilitySnapshot> GetSummary() =>
        Ok(_observability.GetSnapshot());

    [HttpGet("health")]
    public ActionResult<TransportHealthSnapshot> GetHealth() =>
        Ok(_observability.GetHealth());

    [HttpPost("health/evaluate")]
    public async Task<ActionResult<TransportHealthSnapshot>> EvaluateHealth(
        CancellationToken cancellationToken) =>
        Ok(await _observability.EvaluateHealthAsync(cancellationToken));

    [HttpGet("metrics")]
    public ActionResult<TransportTelemetryMetricsSnapshot> GetMetrics() =>
        Ok(_telemetry.GetMetricsSnapshot());

    [HttpGet("traces")]
    public ActionResult<IReadOnlyList<TransportTraceRecord>> GetTraces(
        [FromQuery] int maxCount = 500) =>
        Ok(_telemetry.GetRecentTraces(Math.Clamp(maxCount, 1, 5000)));

    [HttpGet("consistency/latest")]
    public ActionResult<TransportConsistencyReport?> GetLatestConsistency() =>
        Ok(_consistency.GetLastReport());

    [HttpGet("consistency/reports")]
    public ActionResult<IReadOnlyList<TransportConsistencyReport>> GetConsistencyReports(
        [FromQuery] int maxCount = 100) =>
        Ok(_consistency.GetRecentReports(Math.Clamp(maxCount, 1, 100)));

    [HttpPost("consistency/inspect")]
    public async Task<ActionResult<TransportConsistencyReport>> InspectConsistency(
        CancellationToken cancellationToken) =>
        Ok(await _consistency.InspectAsync(cancellationToken));

    [HttpGet("configuration-snapshots")]
    public async Task<ActionResult<IReadOnlyList<TransportConfigurationSnapshot>>> GetConfigurationSnapshots(
        [FromQuery] int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _snapshots.GetAsync(Math.Clamp(maxCount, 1, 500), cancellationToken));

    [HttpPost("configuration-snapshots")]
    public async Task<ActionResult<TransportConfigurationSnapshot>> CreateConfigurationSnapshot(
        [FromBody] CreateTransportConfigurationSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("配置快照必须记录经过认证的创建人");
        var snapshot = await _snapshots.CreateAsync(
            request.Name,
            request.Reason,
            identity.UserId,
            cancellationToken);
        return Ok(snapshot);
    }

    [HttpPost("configuration-snapshots/{snapshotId}/rollback")]
    public async Task<IActionResult> RollbackConfiguration(
        string snapshotId,
        [FromBody] RollbackTransportConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            snapshotId,
            identity,
            cancellationToken);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            var result = await _snapshots.RollbackAsync(
                snapshotId,
                request.ExpectedRuntimeVersion,
                request.ExpectedTuningVersion,
                identity.UserId,
                cancellationToken);
            await _governance.CompleteExecutionAsync(
                request.OperationId,
                identity,
                result.Success,
                result.Success
                    ? $"配置已回滚到快照 {snapshotId}，安全快照 {result.SafetySnapshotId}"
                    : result.Error ?? "配置回滚失败",
                cancellationToken);
            return result.Success ? Ok(result) : Conflict(result);
        }
        catch (Exception ex)
        {
            await _governance.CompleteExecutionAsync(
                request.OperationId,
                identity,
                false,
                ex.Message,
                cancellationToken);
            throw;
        }
    }
}

public sealed record CreateTransportConfigurationSnapshotRequest(string Name, string Reason);

public sealed record RollbackTransportConfigurationRequest(
    string OperationId,
    long ExpectedRuntimeVersion,
    long ExpectedTuningVersion);
