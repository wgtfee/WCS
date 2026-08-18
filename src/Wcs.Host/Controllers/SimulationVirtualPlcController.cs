namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualPlc;

[ApiController]
[Route("api/simulation/virtual-plc")]
public sealed class SimulationVirtualPlcController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualPlcController(
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
        return Inspect(runId, plc => plc.GetStatus(CurrentOffset(runId)));
    }

    [HttpGet("runs/{runId:guid}/blocks")]
    public IActionResult ListBlocks(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, plc => plc.ListBlocks());
    }

    [HttpGet("runs/{runId:guid}/blocks/{plcName}/db/{dbNumber:int}")]
    public IActionResult GetBlock(Guid runId, string plcName, int dbNumber)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, plc => plc.GetBlock($"{plcName}.DB{dbNumber}"));
    }

    [HttpGet("runs/{runId:guid}/faults")]
    public IActionResult ListFaults(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, plc => plc.ListFaults(CurrentOffset(runId)));
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Inspect(runId, plc => plc.ListAudit(take));
    }

    private IActionResult Inspect<T>(Guid runId, Func<VirtualPlcRuntime, T> inspector)
    {
        try
        {
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var plc = new VirtualPlcRuntime(state, _runtime.VirtualPlcOptions);
            return Ok(inspector(plc));
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

    private long CurrentOffset(Guid runId)
    {
        if (!_runtime.Runs.TryGet(runId, out var snapshot))
            throw new KeyNotFoundException($"Simulation run '{runId}' was not found.");
        return snapshot.CurrentOffsetMilliseconds;
    }

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _configuration
                .GetSection(SimulationGovernanceOptions.SectionName)
                .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}
