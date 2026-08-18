namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;

[ApiController]
[Route("api/simulation/virtual-external")]
public sealed class SimulationVirtualExternalController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualExternalController(
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

    [HttpGet("runs/{runId:guid}/endpoints")]
    public IActionResult ListEndpoints(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, offset) => runtime.ListEndpoints(offset));
    }

    [HttpGet("runs/{runId:guid}/endpoints/{endpointId}")]
    public IActionResult GetEndpoint(Guid runId, string endpointId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, offset) => runtime.GetEndpoint(endpointId, offset));
    }

    [HttpGet("runs/{runId:guid}/faults")]
    public IActionResult ListFaults(Guid runId, [FromQuery] bool activeOnly = false)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, offset) => runtime.ListFaults(activeOnly, offset));
    }

    [HttpGet("runs/{runId:guid}/faults/{faultId}")]
    public IActionResult GetFault(Guid runId, string faultId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetFault(faultId));
    }

    [HttpGet("runs/{runId:guid}/requests")]
    public IActionResult ListRequests(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListRequests());
    }

    [HttpGet("runs/{runId:guid}/requests/{requestId}")]
    public IActionResult GetRequest(Guid runId, string requestId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetRequest(requestId));
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        if (take is < 1 or > 1000)
            return BadRequest(new { error = "take must be between 1 and 1000." });
        return Inspect(runId, (runtime, _) => runtime.ListAudit().TakeLast(take).ToArray());
    }

    private IActionResult Inspect<T>(
        Guid runId,
        Func<VirtualExternalRuntime, long, T> inspector)
    {
        try
        {
            if (!_runtime.Runs.TryGet(runId, out var run))
                return NotFound();
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new VirtualExternalRuntime(state, _runtime.VirtualExternalOptions);
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