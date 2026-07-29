namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Host.Simulation;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;

[ApiController]
[Route("api/simulation/scenarios")]
public sealed class SimulationScenarioController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SimulationHostRuntime _runtime;

    public SimulationScenarioController(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
        _runtime = SimulationHostRuntime.GetOrCreate(configuration);
    }

    [HttpGet("runs")]
    public IActionResult ListRuns()
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return Ok(_runtime.Runs.List());
    }

    [HttpGet("runs/{runId:guid}")]
    public IActionResult GetRun(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return _runtime.Runs.TryGet(runId, out var snapshot)
            ? Ok(snapshot)
            : NotFound();
    }

    [HttpPost("runs")]
    public IActionResult CreateRun([FromBody] CreateSimulationRunRequest request)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();

        try
        {
            var (scenario, definition) = ResolveScenario(request);
            return Ok(_runtime.Runs.Create(
                scenario,
                definition,
                request.SpeedFactor,
                request.StartPaused));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "ContentBase64 is invalid." });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("runs/{runId:guid}/step")]
    public async Task<IActionResult> Step(Guid runId, CancellationToken cancellationToken)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return await ExecuteRunCommand(() => _runtime.Runs.StepAsync(runId, cancellationToken));
    }

    [HttpPost("runs/{runId:guid}/advance")]
    public async Task<IActionResult> Advance(
        Guid runId,
        [FromBody] AdvanceSimulationRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return await ExecuteRunCommand(() =>
            _runtime.Runs.AdvanceAsync(runId, request.TargetOffsetMilliseconds, cancellationToken));
    }

    [HttpPost("runs/{runId:guid}/run")]
    public async Task<IActionResult> RunToCompletion(Guid runId, CancellationToken cancellationToken)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return await ExecuteRunCommand(() => _runtime.Runs.RunToCompletionAsync(runId, cancellationToken));
    }

    [HttpPost("runs/{runId:guid}/pause")]
    public IActionResult Pause(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return ExecuteRunCommand(() => _runtime.Runs.Pause(runId));
    }

    [HttpPost("runs/{runId:guid}/resume")]
    public IActionResult Resume(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return ExecuteRunCommand(() => _runtime.Runs.Resume(runId));
    }

    [HttpPost("runs/{runId:guid}/speed")]
    public IActionResult SetSpeed(Guid runId, [FromBody] SetSimulationSpeedRequest request)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return ExecuteRunCommand(() => _runtime.Runs.SetSpeed(runId, request.SpeedFactor));
    }

    [HttpPost("runs/{runId:guid}/cancel")]
    public IActionResult Cancel(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        return ExecuteRunCommand(() => _runtime.Runs.Cancel(runId));
    }

    [HttpGet("runs/{runId:guid}/checkpoint")]
    public IActionResult CreateCheckpoint(Guid runId)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();
        try
        {
            return Ok(_runtime.Runs.CreateCheckpoint(runId));
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

    [HttpPost("replay")]
    public async Task<IActionResult> Replay(
        [FromBody] ReplaySimulationScenarioRequest request,
        CancellationToken cancellationToken)
    {
        if (!GetAccessDecision().Allowed)
            return NotFound();

        try
        {
            var (scenario, definition) = ResolveScenario(request);
            var comparison = await _runtime.Engine.ReplayTwiceAsync(
                scenario,
                definition,
                cancellationToken);
            return comparison.Equivalent
                ? Ok(comparison)
                : Conflict(comparison);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "ContentBase64 is invalid." });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private (RegisteredSimulationScenario Scenario, SimulationScenarioDefinition Definition)
        ResolveScenario(SimulationScenarioContentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScenarioId) ||
            string.IsNullOrWhiteSpace(request.Version) ||
            string.IsNullOrWhiteSpace(request.ContentBase64))
            throw new InvalidOperationException("ScenarioId, Version and ContentBase64 are required.");

        if (!_runtime.Catalog.TryGet(request.ScenarioId, request.Version, out var scenario))
            throw new KeyNotFoundException("The governed scenario version was not found.");

        var content = Convert.FromBase64String(request.ContentBase64);
        var contentHash = SimulationScenarioValidator.ComputeSha256(content);
        if (!string.Equals(contentHash, scenario.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scenario execution content SHA-256 does not match the governed version.");

        var definition = SimulationScenarioDocument.Parse(content, _runtime.EngineOptions);
        return (scenario, definition);
    }

    private async Task<IActionResult> ExecuteRunCommand(
        Func<Task<SimulationRunSnapshot>> command)
    {
        try
        {
            return Ok(await command());
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

    private IActionResult ExecuteRunCommand(Func<SimulationRunSnapshot> command)
    {
        try
        {
            return Ok(command());
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

public abstract class SimulationScenarioContentRequest
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
}

public sealed class CreateSimulationRunRequest : SimulationScenarioContentRequest
{
    public double SpeedFactor { get; set; } = 1;
    public bool StartPaused { get; set; } = true;
}

public sealed class ReplaySimulationScenarioRequest : SimulationScenarioContentRequest;

public sealed class AdvanceSimulationRunRequest
{
    public long TargetOffsetMilliseconds { get; set; }
}

public sealed class SetSimulationSpeedRequest
{
    public double SpeedFactor { get; set; } = 1;
}
