namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;

[ApiController]
[Route("api/simulation/virtual-rgv")]
public sealed class SimulationVirtualRgvController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualRgvController(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
        _runtime = SimulationHostRuntime.GetOrCreate(configuration);
    }

    [HttpGet("runs/{runId:guid}/status")]
    public IActionResult GetStatus(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.GetStatus());
    }

    [HttpGet("runs/{runId:guid}/vehicles")]
    public IActionResult ListVehicles(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.ListVehicles());
    }

    [HttpGet("runs/{runId:guid}/vehicles/{vehicleId}")]
    public IActionResult GetVehicle(Guid runId, string vehicleId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.GetVehicle(vehicleId));
    }

    [HttpGet("runs/{runId:guid}/vehicles/{vehicleId}/transport-snapshot")]
    public IActionResult GetTransportSnapshot(Guid runId, string vehicleId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.GetTransportSnapshot(vehicleId, CurrentTime(runId)));
    }

    [HttpGet("runs/{runId:guid}/segments")]
    public IActionResult ListSegments(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.ListSegments());
    }

    [HttpGet("runs/{runId:guid}/segments/{segmentId}")]
    public IActionResult GetSegment(Guid runId, string segmentId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.GetSegment(segmentId));
    }

    [HttpGet("runs/{runId:guid}/occupancy")]
    public IActionResult ListOccupancy(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.ListOccupancy());
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, runtime => runtime.ListAudit(take));
    }

    private IActionResult Inspect<T>(Guid runId, Func<VirtualRgvRuntime, T> inspector)
    {
        try
        {
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new VirtualRgvRuntime(state, _runtime.VirtualRgvOptions);
            return Ok(inspector(runtime));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private DateTimeOffset CurrentTime(Guid runId)
    {
        if (!_runtime.Runs.TryGet(runId, out var snapshot))
            throw new KeyNotFoundException($"Simulation run '{runId}' was not found.");
        return DateTimeOffset.UnixEpoch.AddMilliseconds(snapshot.CurrentOffsetMilliseconds);
    }

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _configuration
                .GetSection(SimulationGovernanceOptions.SectionName)
                .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}
