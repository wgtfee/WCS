namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wcs.Simulator.Governance;

[ApiController]
[Route("api/simulation/governance")]
public sealed class SimulationGovernanceController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IOptions<SimulationGovernanceOptions> _options;
    private readonly SimulationScenarioRegistry _registry;

    public SimulationGovernanceController(
        IHostEnvironment environment,
        IConfiguration configuration,
        IOptions<SimulationGovernanceOptions> options,
        SimulationScenarioRegistry registry)
    {
        _environment = environment;
        _configuration = configuration;
        _options = options;
        _registry = registry;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var decision = GetAccessDecision();
        if (!decision.Allowed)
            return NotFound();

        var options = _options.Value;
        return Ok(new
        {
            enabled = true,
            environment = _environment.EnvironmentName,
            scenarioDirectory = options.ScenarioDirectory,
            maximumScenarioBytes = options.MaximumScenarioBytes,
            maximumEvidenceRecords = options.MaximumEvidenceRecords,
            registeredScenarioVersions = _registry.List().Count,
            productionAllowed = false,
            controlWritesAllowed = false,
            decision.Code
        });
    }

    [HttpGet("scenarios")]
    public IActionResult GetRegisteredScenarios()
    {
        var decision = GetAccessDecision();
        if (!decision.Allowed)
            return NotFound();

        return Ok(_registry.List());
    }

    [HttpPost("scenarios/validate")]
    public IActionResult ValidateAndRegister([FromBody] ValidateSimulationScenarioRequest request)
    {
        var decision = GetAccessDecision();
        if (!decision.Allowed)
            return NotFound();
        if (request.Manifest is null || string.IsNullOrWhiteSpace(request.ContentBase64))
            return BadRequest(new { error = "Manifest and ContentBase64 are required." });

        try
        {
            var content = Convert.FromBase64String(request.ContentBase64);
            var registered = _registry.Register(
                new SimulationScenarioPackage(request.Manifest, content),
                _options.Value);
            return Ok(registered);
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

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            _options.Value,
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}

public sealed class ValidateSimulationScenarioRequest
{
    public SimulationScenarioManifest? Manifest { get; set; }
    public string ContentBase64 { get; set; } = string.Empty;
}
