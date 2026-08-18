namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/production")]
public sealed class TransportProductionController : ControllerBase
{
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportProductionTrendService _trends;
    private readonly ITransportFaultTakeoverService _takeover;
    private readonly ITransportOperationGovernanceService _governance;

    public TransportProductionController(
        ITransportProductionTuningService tuning,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportProductionDispatchService production,
        ITransportProductionTrendService trends,
        ITransportFaultTakeoverService takeover,
        ITransportOperationGovernanceService governance)
    {
        _tuning = tuning;
        _stations = stations;
        _singleTrack = singleTrack;
        _production = production;
        _trends = trends;
        _takeover = takeover;
        _governance = governance;
    }

    [HttpGet("tuning")]
    public ActionResult<TransportProductionTuningOptions> GetTuning() => Ok(_tuning.Current);

    [HttpPut("tuning")]
    public async Task<ActionResult<TransportProductionTuningSaveResult>> SaveTuning(
        [FromBody] SaveTransportProductionTuningRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            "production:tuning",
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _tuning.SaveAsync(
            request.Options,
            request.ExpectedVersion,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "生产调度参数已更新" : result.Error ?? "参数更新失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("stations")]
    public ActionResult<IReadOnlyList<TransportStationRuntimeSnapshot>> GetStations() =>
        Ok(_stations.GetAll());

    [HttpPut("stations/{stationId}")]
    public async Task<IActionResult> SaveStation(
        string stationId,
        [FromBody] SaveTransportStationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            $"production-station:{stationId}",
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            await _stations.SaveDefinitionAsync(
                request.Definition with { StationId = stationId },
                cancellationToken).ConfigureAwait(false);
            await CompleteAsync(request.OperationId, actor, true, "生产站点定义已保存", cancellationToken).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            await CompleteAsync(request.OperationId, actor, false, ex.Message, cancellationToken).ConfigureAwait(false);
            return Conflict(ex.Message);
        }
    }

    [HttpPost("stations/{stationId}/runtime")]
    public IActionResult UpdateStationRuntime(
        string stationId,
        [FromBody] UpdateTransportStationRuntimeRequest request)
    {
        _stations.UpdateOccupancy(stationId, request.OccupiedCount);
        if (request.QueuedTaskCount.HasValue)
            _stations.SetQueuedTaskCount(stationId, request.QueuedTaskCount.Value);
        return Ok(_stations.GetAll().FirstOrDefault(x =>
            string.Equals(x.StationId, stationId, StringComparison.Ordinal)));
    }

    [HttpGet("single-track")]
    public ActionResult<IReadOnlyList<TransportSingleTrackSectionSnapshot>> GetSingleTrack() =>
        Ok(_singleTrack.GetSnapshots());

    [HttpPut("single-track/{sectionId}")]
    public async Task<IActionResult> SaveSingleTrack(
        string sectionId,
        [FromBody] SaveTransportSingleTrackRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            $"single-track:{sectionId}",
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            await _singleTrack.SaveDefinitionAsync(
                request.Definition with { SectionId = sectionId },
                cancellationToken).ConfigureAwait(false);
            await CompleteAsync(request.OperationId, actor, true, "单轨会车区段已保存", cancellationToken).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            await CompleteAsync(request.OperationId, actor, false, ex.Message, cancellationToken).ConfigureAwait(false);
            return Conflict(ex.Message);
        }
    }

    [HttpGet("queue")]
    public ActionResult<IReadOnlyList<TransportProductionQueueItem>> GetQueue() =>
        Ok(_production.GetQueue());

    [HttpPost("queue")]
    public ActionResult<TransportProductionQueueItem> Enqueue(
        [FromBody] TransportProductionDispatchRequest request) =>
        Ok(_production.Enqueue(request));

    [HttpPost("queue/{requestId}/cancel")]
    public IActionResult Cancel(string requestId) =>
        _production.Cancel(requestId) ? Ok() : Conflict("任务不存在、已派单或已取消");

    [HttpPost("queue/{requestId}/complete")]
    public IActionResult Complete(string requestId) =>
        _production.Complete(requestId) ? Ok() : Conflict("任务未处于已派单状态或底层派单不存在");

    [HttpPost("dispatch-cycle")]
    public async Task<ActionResult<TransportProductionDispatchCycleResult>> DispatchCycle(
        CancellationToken cancellationToken) =>
        Ok(await _production.DispatchCycleAsync(cancellationToken).ConfigureAwait(false));

    [HttpGet("dry-run")]
    public ActionResult<TransportProductionDryRunReport> DryRun() => Ok(_production.DryRun());

    [HttpGet("decisions")]
    public ActionResult<IReadOnlyList<TransportDispatchDecisionFrame>> GetDecisions(
        [FromQuery] int maxCount = 500) =>
        Ok(_production.GetDecisions(maxCount));

    [HttpGet("trends")]
    public ActionResult<TransportProductionTrendSummary> GetTrends(
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null)
    {
        var to = toUtc ?? DateTime.UtcNow;
        var from = fromUtc ?? to.AddHours(-24);
        return Ok(_trends.GetSummary(from, to));
    }

    [HttpPost("trends/capture")]
    public async Task<ActionResult<TransportProductionTrendPoint>> CaptureTrend(
        CancellationToken cancellationToken) =>
        Ok(await _trends.CaptureAsync(cancellationToken).ConfigureAwait(false));

    [HttpGet("fault-takeover")]
    public ActionResult<TransportFaultTakeoverReport> GetFaultTakeover() =>
        Ok(_takeover.GetLastReport());

    [HttpPost("fault-takeover/evaluate")]
    public async Task<ActionResult<TransportFaultTakeoverReport>> EvaluateFaultTakeover(
        CancellationToken cancellationToken) =>
        Ok(await _takeover.EvaluateAsync(cancellationToken).ConfigureAwait(false));

    private Task CompleteAsync(
        string operationId,
        TransportOperatorIdentity actor,
        bool success,
        string message,
        CancellationToken cancellationToken) =>
        _governance.CompleteExecutionAsync(
            operationId,
            actor,
            success,
            message,
            cancellationToken);
}

public sealed record SaveTransportProductionTuningRequest(
    string OperationId,
    long ExpectedVersion,
    TransportProductionTuningOptions Options);

public sealed record SaveTransportStationRequest(
    string OperationId,
    TransportStationDefinition Definition);

public sealed record UpdateTransportStationRuntimeRequest(
    int OccupiedCount,
    int? QueuedTaskCount);

public sealed record SaveTransportSingleTrackRequest(
    string OperationId,
    TransportSingleTrackSectionDefinition Definition);
