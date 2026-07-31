namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualTraffic;

[ApiController]
[Route("api/simulation/virtual-traffic")]
public sealed class SimulationVirtualTrafficController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualTrafficController(
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
        return Inspect(runId, (runtime, offset) => runtime.GetStatus(offset));
    }

    [HttpGet("runs/{runId:guid}/zones")]
    public IActionResult ListZones(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListZones());
    }

    [HttpGet("runs/{runId:guid}/zones/{zoneId}")]
    public IActionResult GetZone(Guid runId, string zoneId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetZone(zoneId));
    }

    [HttpGet("runs/{runId:guid}/reservations")]
    public IActionResult ListReservations(Guid runId, [FromQuery] bool activeOnly = true)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, offset) => runtime.ListReservations(activeOnly, offset));
    }

    [HttpGet("runs/{runId:guid}/waiting")]
    public IActionResult ListWaitingRequests(Guid runId, [FromQuery] bool activeOnly = true)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListWaitingRequests(activeOnly));
    }

    [HttpGet("runs/{runId:guid}/wait-graph")]
    public IActionResult ListWaitGraph(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListWaitEdges());
    }

    [HttpGet("runs/{runId:guid}/deadlocks")]
    public IActionResult ListDeadlocks(Guid runId, [FromQuery] bool activeOnly = true)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListDeadlocks(activeOnly));
    }

    [HttpGet("runs/{runId:guid}/deadlocks/{deadlockId}")]
    public IActionResult GetDeadlock(Guid runId, string deadlockId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetDeadlock(deadlockId));
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListAudit(take));
    }

    private IActionResult Inspect<T>(
        Guid runId,
        Func<VirtualTrafficRuntime, long, T> inspector)
    {
        try
        {
            if (!_runtime.Runs.TryGet(runId, out var run))
                return NotFound();
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new VirtualTrafficRuntime(
                state,
                _runtime.VirtualTrafficOptions,
                _runtime.VirtualRgvOptions);
            return Ok(inspector(runtime, run.CurrentOffsetMilliseconds));
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

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _configuration
                .GetSection(SimulationGovernanceOptions.SectionName)
                .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}
