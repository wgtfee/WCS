namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Simulator.Governance;

[ApiController]
[Route("api/simulation/governance")]
public sealed class SimulationGovernanceController : ControllerBase
{
    private static readonly SimulationScenarioRegistry Registry = new();

    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public SimulationGovernanceController(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var decision = GetAccessDecision();
        if (!decision.Allowed)
            return NotFound();

        var options = GetOptions();
        return Ok(new
        {
            enabled = true,
            environment = _environment.EnvironmentName,
            scenarioDirectory = options.ScenarioDirectory,
            maximumScenarioBytes = options.MaximumScenarioBytes,
            maximumEvidenceRecords = options.MaximumEvidenceRecords,
            registeredScenarioVersions = Registry.List().Count,
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

        return Ok(Registry.List());
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
            var registered = Registry.Register(
                new SimulationScenarioPackage(request.Manifest, content),
                GetOptions());
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

    private SimulationGovernanceOptions GetOptions()
    {
        var options = _configuration
            .GetSection(SimulationGovernanceOptions.SectionName)
            .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions();
        options.Validate();
        return options;
    }

    private SimulationAccessDecision GetAccessDecision() =>
        SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            GetOptions(),
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
}

public sealed class ValidateSimulationScenarioRequest
{
    public SimulationScenarioManifest? Manifest { get; set; }
    public string ContentBase64 { get; set; } = string.Empty;
}
