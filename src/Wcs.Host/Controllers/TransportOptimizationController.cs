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

    public TransportOptimizationController(
        ITransportChargingCoordinator charging,
        ITransportTaskReassignmentService reassignments,
        ITransportPerformanceService performance)
    {
        _charging = charging;
        _reassignments = reassignments;
        _performance = performance;
    }

    [HttpGet("charging/policy")]
    public ActionResult<TransportChargingPolicy> GetChargingPolicy() =>
        Ok(_charging.Policy);

    [HttpGet("charging/stations")]
    public ActionResult<IReadOnlyList<TransportChargingStationSnapshot>> GetChargingStations() =>
        Ok(_charging.GetStations());

    [HttpPost("charging/stations")]
    public IActionResult RegisterChargingStation(
        [FromBody] TransportChargingStationDefinition station)
    {
        _charging.RegisterStation(station);
        return CreatedAtAction(nameof(GetChargingStations), new { stationId = station.StationId }, station);
    }

    [HttpDelete("charging/stations/{stationId}")]
    public IActionResult RemoveChargingStation(string stationId) =>
        _charging.RemoveStation(stationId) ? NoContent() : Conflict();

    [HttpGet("charging/plans")]
    public ActionResult<IReadOnlyList<TransportChargingPlan>> GetChargingPlans() =>
        Ok(_charging.GetPlans());

    [HttpPost("charging/evaluate")]
    public ActionResult<IReadOnlyList<TransportChargingEvaluation>> EvaluateCharging() =>
        Ok(_charging.EvaluateFleet());

    [HttpPost("charging/vehicles/{vehicleId}/evaluate")]
    public ActionResult<TransportChargingEvaluation> EvaluateVehicle(string vehicleId) =>
        Ok(_charging.EvaluateVehicle(vehicleId));

    [HttpPost("charging/plans/{planId}/arrived")]
    public IActionResult ConfirmChargingArrived(string planId) =>
        _charging.ConfirmArrived(planId) ? Ok() : Conflict();

    [HttpPost("charging/plans/{planId}/complete")]
    public IActionResult CompleteCharging(
        string planId,
        [FromBody] CompleteChargingRequest request) =>
        _charging.Complete(planId, request.BatteryPercent) ? Ok() : Conflict();

    [HttpPost("charging/plans/{planId}/cancel")]
    public IActionResult CancelCharging(
        string planId,
        [FromBody] CancelChargingRequest? request = null) =>
        _charging.Cancel(planId, request?.Reason) ? Ok() : Conflict();

    [HttpGet("reassignments")]
    public ActionResult<IReadOnlyList<TransportTaskReassignmentRecord>> GetReassignments() =>
        Ok(_reassignments.GetHistory());

    [HttpPost("executions/{requestId}/reassign")]
    public async Task<ActionResult<TransportTaskReassignmentResult>> Reassign(
        string requestId,
        [FromBody] TransportReassignmentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _reassignments.ReassignAsync(
            requestId,
            request.Reason,
            request.StartImmediately,
            cancellationToken);

        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("metrics")]
    public ActionResult<TransportPerformanceSnapshot> GetMetrics() =>
        Ok(_performance.GetSnapshot());
}

public sealed record CompleteChargingRequest(int BatteryPercent);
public sealed record CancelChargingRequest(string? Reason);
public sealed record TransportReassignmentRequest(
    string Reason,
    bool StartImmediately = true);
