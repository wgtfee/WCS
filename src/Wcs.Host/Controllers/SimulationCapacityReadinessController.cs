namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.CapacityReadiness;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;

[ApiController]
[Route("api/simulation/capacity-readiness")]
public sealed class SimulationCapacityReadinessController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationCapacityReadinessController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
        _runtime = SimulationHostRuntime.GetOrCreate(configuration);
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        var o = _runtime.CapacityReadinessOptions;
        return Ok(new
        {
            stage = "S8",
            simulationOnly = true,
            readyForRealHil = false,
            realHilExecuted = false,
            mechanicalSafetyAccepted = false,
            siteAccepted = false,
            eightHourVirtualDurationMilliseconds = o.EightHourVirtualDurationMilliseconds,
            twentyFourHourVirtualDurationMilliseconds = o.TwentyFourHourVirtualDurationMilliseconds,
            o.MaximumMissionsPerProfile,
            o.MaximumConcurrentMissions,
            o.MaximumSegmentsPerMission,
            o.MaximumSamplesPerProfile,
            o.MaximumWallClockMilliseconds,
            o.MaximumRssGrowthBytes,
            controlWritesAllowed = false
        });
    }

    [HttpGet("runs/{runId:guid}/profiles/{profileId}")]
    public IActionResult GetProfile(Guid runId, string profileId)
    {
        if (!GetAccessDecision().Allowed) return NotFound();
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 128) return BadRequest(new { error = "profileId must be 1..128 characters." });
        try
        {
            if (!_runtime.Runs.TryGet(runId, out _)) return NotFound();
            var checkpoint = _runtime.Runs.CreateCheckpoint(runId);
            var state = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _runtime.EngineOptions);
            var runtime = new CapacityReadinessRuntime(state, _runtime.EngineOptions, _runtime.CapacityReadinessOptions,
                _runtime.VirtualIntegrationOptions, _runtime.VirtualPlcOptions, _runtime.VirtualRgvOptions,
                _runtime.VirtualTrafficOptions, _runtime.VirtualExternalOptions, _runtime.VirtualHealthOptions);
            var result = runtime.TryGetProfileResult(profileId);
            return result is null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _configuration.GetSection(SimulationGovernanceOptions.SectionName).Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}
