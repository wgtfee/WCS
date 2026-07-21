namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport")]
public sealed class TransportSchedulingController : ControllerBase
{
    private readonly IUnifiedTransportDispatchEngine _dispatch;
    private readonly ITransportExecutionEngine _execution;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly IRouteReservationManager _reservations;
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportRecoveryCoordinator _recovery;
    private readonly ITransportCommandDispatcher _commandDispatcher;

    public TransportSchedulingController(
        IUnifiedTransportDispatchEngine dispatch,
        ITransportExecutionEngine execution,
        ITransportVehicleRegistry vehicles,
        IRouteReservationManager reservations,
        ITransportStateStore stateStore,
        ITransportRecoveryCoordinator recovery,
        ITransportCommandDispatcher commandDispatcher)
    {
        _dispatch = dispatch;
        _execution = execution;
        _vehicles = vehicles;
        _reservations = reservations;
        _stateStore = stateStore;
        _recovery = recovery;
        _commandDispatcher = commandDispatcher;
    }

    [HttpGet("vehicles")]
    public ActionResult<IReadOnlyList<TransportVehicleSnapshot>> GetVehicles() => Ok(_vehicles.GetAll());

    [HttpGet("executions")]
    public ActionResult<IReadOnlyList<TransportExecutionSnapshot>> GetExecutions() => Ok(_execution.GetAll());

    [HttpGet("reservations")]
    public ActionResult<IReadOnlyList<RouteReservation>> GetReservations() => Ok(_reservations.GetActiveReservations());

    [HttpGet("runtime-snapshot")]
    public async Task<ActionResult<TransportRuntimeSnapshot>> GetRuntimeSnapshot(CancellationToken cancellationToken) =>
        Ok(await _stateStore.LoadAsync(cancellationToken));

    [HttpPost("recover")]
    public async Task<ActionResult<TransportRecoveryReport>> Recover(CancellationToken cancellationToken) =>
        Ok(await _recovery.RecoverAsync(cancellationToken));

    [HttpPost("commands/dispatch")]
    public async Task<ActionResult<TransportCommandRecord>> DispatchCommand(
        [FromBody] TransportCommandDispatchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _commandDispatcher.DispatchAsync(
            request.Command,
            request.VehicleKind,
            request.MaxRetries,
            cancellationToken);

        return result.Status is TransportCommandStatus.Completed or TransportCommandStatus.Acknowledged
            ? Ok(result)
            : Conflict(result);
    }

    [HttpPost("dispatch")]
    public async Task<ActionResult<TransportDispatchResult>> Dispatch(
        [FromBody] TransportDispatchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _dispatch.DispatchAsync(request, cancellationToken);
        if (result.Success && result.Assignment is not null)
            _execution.Create(result.Assignment.RequestId);

        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpPost("executions/{requestId}/start")]
    public ActionResult<TransportExecutionResult> Start(string requestId) => ToActionResult(_execution.Start(requestId));

    [HttpPost("executions/{requestId}/loaded")]
    public ActionResult<TransportExecutionResult> ConfirmLoaded(string requestId) => ToActionResult(_execution.ConfirmLoaded(requestId));

    [HttpPost("executions/{requestId}/unloaded")]
    public ActionResult<TransportExecutionResult> ConfirmUnloaded(string requestId) => ToActionResult(_execution.ConfirmUnloaded(requestId));

    [HttpPost("executions/{requestId}/pause")]
    public ActionResult<TransportExecutionResult> Pause(string requestId) => ToActionResult(_execution.Pause(requestId));

    [HttpPost("executions/{requestId}/resume")]
    public ActionResult<TransportExecutionResult> Resume(string requestId) => ToActionResult(_execution.Resume(requestId));

    [HttpPost("executions/{requestId}/fault")]
    public ActionResult<TransportExecutionResult> Fault(string requestId, [FromBody] TransportFaultRequest request) =>
        ToActionResult(_execution.Fault(requestId, request.Reason));

    [HttpPost("executions/{requestId}/cancel")]
    public ActionResult<TransportExecutionResult> Cancel(string requestId, [FromBody] TransportCancelRequest? request = null) =>
        ToActionResult(_execution.Cancel(requestId, request?.Reason));

    [HttpPost("position-feedback")]
    public ActionResult<TransportExecutionResult> PositionFeedback([FromBody] TransportPositionFeedback feedback) =>
        ToActionResult(_execution.ApplyPositionFeedback(feedback));

    [HttpGet("vehicles/{vehicleId}/commands")]
    public ActionResult<IReadOnlyList<TransportExecutionCommand>> DequeueCommands(
        string vehicleId,
        [FromQuery] int maxCount = 20) => Ok(_execution.DequeueCommands(vehicleId, maxCount));

    private ActionResult<TransportExecutionResult> ToActionResult(TransportExecutionResult result) =>
        result.Success ? Ok(result) : Conflict(result);
}

public sealed record TransportFaultRequest(string Reason);
public sealed record TransportCancelRequest(string? Reason);
public sealed record TransportCommandDispatchRequest(
    TransportExecutionCommand Command,
    TransportVehicleKind VehicleKind,
    int MaxRetries = 3);
