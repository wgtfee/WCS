namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualIntegration;

[ApiController]
[Route("api/simulation/virtual-integration")]
public sealed class SimulationVirtualIntegrationController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualIntegrationController(
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
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetStatus());
    }

    [HttpGet("runs/{runId:guid}/missions")]
    public IActionResult ListMissions(Guid runId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, (runtime, _) => runtime.ListMissions());
    }

    [HttpGet("runs/{runId:guid}/missions/{missionId}")]
    public IActionResult GetMission(Guid runId, string missionId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, (runtime, _) => runtime.GetMission(missionId));
    }

    [HttpGet("runs/{runId:guid}/missions/{missionId}/consistency")]
    public IActionResult GetConsistency(Guid runId, string missionId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, (runtime, offset) => runtime.GetConsistency(missionId, offset));
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        if (take is < 1 or > 1000)
            return BadRequest(new { error = "take must be between 1 and 1000." });
        return Inspect(runId, (runtime, _) => runtime.ListAudit().TakeLast(take).ToArray());
    }

    private IActionResult Inspect<T>(
        Guid runId,
        Func<VirtualIntegrationRuntime, long, T> inspector)
    {
        try
        {
            if (!_runtime.Runs.TryGet(runId, out var run))
                return NotFound();
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new VirtualIntegrationRuntime(
                state,
                _runtime.VirtualIntegrationOptions,
                _runtime.VirtualPlcOptions,
                _runtime.VirtualRgvOptions,
                _runtime.VirtualTrafficOptions,
                _runtime.VirtualExternalOptions,
                _runtime.VirtualHealthOptions);
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
