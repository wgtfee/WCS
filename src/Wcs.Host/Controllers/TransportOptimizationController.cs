namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/optimization")]
public sealed class TransportOptimizationController : ControllerBase
{
    private readonly ITransportChargingCoordinator _charging;
    private readonly ITransportTaskReassignmentService _reassignments;
    private readonly ITransportPerformanceService _performance;
    private readonly ITransportOperationGovernanceService _governance;

    public TransportOptimizationController(
        ITransportChargingCoordinator charging,
        ITransportTaskReassignmentService reassignments,
        ITransportPerformanceService performance,
        ITransportOperationGovernanceService governance)
    {
        _charging = charging;
        _reassignments = reassignments;
        _performance = performance;
        _governance = governance;
    }

    [HttpGet("charging/policy")]
    public ActionResult<TransportChargingPolicy> GetChargingPolicy() => Ok(_charging.Policy);

    [HttpGet("charging/stations")]
    public ActionResult<IReadOnlyList<TransportChargingStationSnapshot>> GetChargingStations() => Ok(_charging.GetStations());

    [HttpPost("charging/stations")]
    public IActionResult RegisterChargingStation([FromBody] TransportChargingStationDefinition station) =>
        Conflict(new
        {
            message = "第六阶段起充电站必须通过版本化调度配置和独立审批修改",
            configurationEndpoint = "/api/transport/administration/configuration/{operationId}",
            station.StationId
        });

    [HttpDelete("charging/stations/{stationId}")]
    public IActionResult RemoveChargingStation(string stationId) =>
        Conflict(new
        {
            message = "第六阶段起充电站必须通过版本化调度配置和独立审批修改",
            configurationEndpoint = "/api/transport/administration/configuration/{operationId}",
            stationId
        });

    [HttpGet("charging/plans")]
    public ActionResult<IReadOnlyList<TransportChargingPlan>> GetChargingPlans() => Ok(_charging.GetPlans());

    [HttpPost("charging/evaluate")]
    public ActionResult<IReadOnlyList<TransportChargingEvaluation>> EvaluateCharging() => Ok(_charging.EvaluateFleet());

    [HttpPost("charging/vehicles/{vehicleId}/evaluate")]
    public ActionResult<TransportChargingEvaluation> EvaluateVehicle(string vehicleId) => Ok(_charging.EvaluateVehicle(vehicleId));

    [HttpPost("charging/plans/{planId}/arrived")]
    public IActionResult ConfirmChargingArrived(string planId) => _charging.ConfirmArrived(planId) ? Ok() : Conflict();

    [HttpPost("charging/plans/{planId}/complete")]
    public IActionResult CompleteCharging(string planId, [FromBody] CompleteChargingRequest request) =>
        _charging.Complete(planId, request.BatteryPercent) ? Ok() : Conflict();

    [HttpPost("charging/plans/{planId}/cancel")]
    public IActionResult CancelCharging(string planId, [FromBody] CancelChargingRequest? request = null) =>
        _charging.Cancel(planId, request?.Reason) ? Ok() : Conflict();

    [HttpGet("reassignments")]
    public ActionResult<IReadOnlyList<TransportTaskReassignmentRecord>> GetReassignments() => Ok(_reassignments.GetHistory());

    [HttpPost("executions/{requestId}/reassign")]
    public async Task<ActionResult<TransportTaskReassignmentResult>> Reassign(
        string requestId,
        [FromBody] TransportReassignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId))
            return BadRequest("故障换车必须提供已审批的 OperationId");

        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("故障换车必须使用经过认证的用户身份");

        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ReassignTask,
            requestId,
            identity,
            cancellationToken);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            var result = await _reassignments.ReassignAsync(
                requestId,
                request.Reason,
                request.StartImmediately,
                cancellationToken);

            await _governance.CompleteExecutionAsync(
                request.OperationId,
                identity,
                result.Success,
                result.Record.Reason,
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

    [HttpGet("metrics")]
    public ActionResult<TransportPerformanceSnapshot> GetMetrics() => Ok(_performance.GetSnapshot());
}

public sealed record CompleteChargingRequest(int BatteryPercent);
public sealed record CancelChargingRequest(string? Reason);
public sealed record TransportReassignmentRequest(
    string Reason,
    bool StartImmediately = true,
    string? OperationId = null);
