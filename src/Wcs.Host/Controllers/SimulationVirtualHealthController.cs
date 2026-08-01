namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualHealth;

[ApiController]
[Route("api/simulation/virtual-health")]
public sealed class SimulationVirtualHealthController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationVirtualHealthController(
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
        return Inspect(runId, runtime => runtime.GetStatus());
    }

    [HttpGet("runs/{runId:guid}/assets")]
    public IActionResult ListAssets(Guid runId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.ListAssets());
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}")]
    public IActionResult GetAsset(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.GetAsset(assetId));
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}/samples")]
    public IActionResult ListSamples(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.ListSamples(assetId));
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}/feature")]
    public IActionResult GetFeature(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.GetFeatureSnapshot(assetId));
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}/trend")]
    public IActionResult GetTrend(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.GetTrend(assetId));
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}/forecasts")]
    public IActionResult ListForecasts(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.ListForecasts(assetId));
    }

    [HttpGet("runs/{runId:guid}/assets/{assetId}/outcomes")]
    public IActionResult ListOutcomes(Guid runId, string assetId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        return Inspect(runId, runtime => runtime.ListOutcomes(assetId));
    }

    [HttpGet("runs/{runId:guid}/audit")]
    public IActionResult ListAudit(Guid runId, [FromQuery] int take = 100)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        if (take is < 1 or > 1000)
            return BadRequest(new { error = "take must be between 1 and 1000." });
        return Inspect(runId, runtime => runtime.ListAudit().TakeLast(take).ToArray());
    }

    private IActionResult Inspect<T>(Guid runId, Func<VirtualHealthRuntime, T> inspector)
    {
        try
        {
            if (!_runtime.Runs.TryGet(runId, out _))
                return NotFound();
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new VirtualHealthRuntime(state, _runtime.VirtualHealthOptions);
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

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _configuration
                .GetSection(SimulationGovernanceOptions.SectionName)
                .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}
