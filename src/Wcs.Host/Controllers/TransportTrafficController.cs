namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/traffic")]
public sealed class TransportTrafficController : ControllerBase
{
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportDeadlockService _deadlocks;

    public TransportTrafficController(
        ITransportTrafficCoordinator traffic,
        ITransportDeadlockService deadlocks)
    {
        _traffic = traffic;
        _deadlocks = deadlocks;
    }

    [HttpGet]
    public ActionResult<TransportTrafficSnapshot> GetSnapshot() => Ok(_traffic.GetSnapshot());

    [HttpGet("resources")]
    public ActionResult<IReadOnlyList<TransportTrafficResourceDefinition>> GetResources() =>
        Ok(_traffic.GetResources());

    [HttpGet("holds")]
    public ActionResult<IReadOnlyList<TransportTrafficHold>> GetHolds() =>
        Ok(_traffic.GetHolds());

    [HttpGet("waits")]
    public ActionResult<IReadOnlyList<TransportTrafficWait>> GetWaits() =>
        Ok(_traffic.GetWaits());

    [HttpGet("incidents")]
    public ActionResult<IReadOnlyList<TransportTrafficIncident>> GetIncidents() =>
        Ok(_traffic.GetIncidents());

    [HttpGet("deadlocks")]
    public ActionResult<IReadOnlyList<TransportDeadlockCycle>> GetDeadlocks() =>
        Ok(_deadlocks.Detect());

    [HttpPost("resources")]
    public ActionResult RegisterResource([FromBody] TransportTrafficResourceDefinition definition)
    {
        _traffic.RegisterResource(definition);
        return Ok(definition);
    }

    [HttpDelete("resources/{resourceId}")]
    public ActionResult RemoveResource(string resourceId) =>
        _traffic.RemoveResource(resourceId)
            ? NoContent()
            : Conflict(new { message = "资源不存在或仍有车辆确认占用" });

    [HttpPost("occupancy")]
    public ActionResult MarkOccupancy([FromBody] TransportTrafficOccupancyRequest request) =>
        _traffic.MarkOccupancy(request.OwnerId, request.ResourceId, request.Occupied)
            ? Ok()
            : NotFound(new { message = "未找到对应交通资源持有记录" });

    [HttpPost("deadlocks/{cycleId}/resolve")]
    public ActionResult<TransportDeadlockResolution> ResolveDeadlock(string cycleId)
    {
        var result = _deadlocks.Resolve(cycleId);
        return result.Status == TransportDeadlockResolutionStatus.CycleNotFound
            ? NotFound(result)
            : Ok(result);
    }
}

public sealed record TransportTrafficOccupancyRequest(
    string OwnerId,
    string ResourceId,
    bool Occupied);
