namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;
using Wcs.Optimization;

/// <summary>
/// IDI-P5 read-only inspection API. It exposes persisted experiment definitions,
/// rankings and evidence only. Experiment execution and production policy replacement
/// are deliberately not exposed by HTTP.
/// </summary>
[ApiController]
[Route("api/digital-twin-optimizer")]
public sealed class DigitalTwinOptimizerController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public DigitalTwinOptimizerController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!TryAllowP5(out var access)) return NotFound();
        try
        {
            var recovery = await GetFactory().CreateRecovery().RecoverAsync(ct);
            return Ok(new
            {
                stage = "IDI-P5",
                environment = _environment.EnvironmentName,
                mode = access.EffectiveMode.ToString(),
                maximumAutomationLevel = access.EffectiveMaximumAutomationLevel.ToString(),
                controlWriteAllowed = false,
                autoProductionPolicyReplacementAllowed = false,
                productionAutomationAllowed = false,
                executionApiExposed = false,
                persistence = "SQL Server",
                requiredSimulationStages = OptimizationGovernance.RequiredSimulationStages.Select(static stage => stage.ToString()).ToArray(),
                requiredLoadCases = OptimizationGovernance.RequiredLoadCases.Select(static loadCase => loadCase.ToString()).ToArray(),
                determinismRoundsPerInput = OptimizationGovernance.DeterminismRoundsPerInput,
                recovery
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("policy-kinds")]
    public IActionResult GetPolicyKinds()
    {
        if (!TryAllowP5(out _)) return NotFound();
        return Ok(new
        {
            stage = "IDI-P5",
            readOnly = true,
            minimumCandidateCount = OptimizationGovernance.MinimumCandidateCount,
            maximumCandidateCount = OptimizationGovernance.MaximumCandidateCount,
            autoProductionPolicyReplacementAllowed = false,
            values = Enum.GetValues<OptimizationPolicyKind>().Select(static value => value.ToString()).ToArray()
        });
    }

    [HttpGet("experiments")]
    public async Task<IActionResult> ListExperiments([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!TryAllowP5(out _)) return NotFound();
        if (limit is < 1 or > 500) return BadRequest("limit must be in [1,500].");
        try
        {
            var values = await GetFactory().CreateStore().ListAsync(limit, ct);
            return Ok(new
            {
                stage = "IDI-P5",
                readOnly = true,
                controlWriteAllowed = false,
                autoProductionPolicyReplacementAllowed = false,
                productionAutomationAllowed = false,
                values
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("experiments/{experimentId}/definition")]
    public async Task<IActionResult> GetDefinition(string experimentId, CancellationToken ct)
    {
        if (!TryAllowP5(out _)) return NotFound();
        if (!ValidIdentifier(experimentId)) return BadRequest("experimentId is required and must be <= 120 characters.");
        try
        {
            var value = await GetFactory().CreateStore().GetDefinitionAsync(experimentId, ct);
            return value is null ? NotFound() : Ok(new
            {
                stage = "IDI-P5",
                readOnly = true,
                controlWriteAllowed = false,
                value
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("experiments/{experimentId}/result")]
    public async Task<IActionResult> GetResult(string experimentId, CancellationToken ct)
    {
        if (!TryAllowP5(out _)) return NotFound();
        if (!ValidIdentifier(experimentId)) return BadRequest("experimentId is required and must be <= 120 characters.");
        try
        {
            var value = await GetFactory().CreateStore().GetResultAsync(experimentId, ct);
            return value is null ? NotFound() : Ok(new
            {
                stage = "IDI-P5",
                readOnly = true,
                controlWriteAllowed = false,
                autoProductionPolicyReplacementAllowed = false,
                productionAutomationAllowed = false,
                value
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    private OptimizationPersistenceFactory GetFactory()
    {
        var connectionString = _configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new OptimizationPersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowP5(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Digital Twin Optimizer read-only operation failed closed",
        detail: ex.Message);

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120;
}
