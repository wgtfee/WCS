namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;

/// <summary>
/// IDI-P6 read-only governance/evidence surface. This controller cannot evaluate,
/// approve, enable, execute, rollback, release, reset, or write production control.
/// </summary>
[ApiController]
[Route("api/bounded-automation-readiness")]
public sealed class BoundedAutomationReadinessController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public BoundedAutomationReadinessController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!TryAllowReadOnly(out var access)) return NotFound();
        return Ok(new
        {
            stage = "IDI-P6",
            environment = _environment.EnvironmentName,
            mode = access.EffectiveMode.ToString(),
            hostMaximumAutomationLevel = access.EffectiveMaximumAutomationLevel.ToString(),
            finalClaim = BoundedAutomationReadinessGovernance.SoftwareOnlyClaim,
            defaultsDisabled = true,
            productionEnablementAllowed = false,
            controlWriteAllowed = false,
            executionApiExposed = false,
            approvalApiExposed = false,
            rollbackExecutionApiExposed = false,
            realSiteEvidenceRequiredForL2L3 = true,
            realHilEvidenceRequiredForL2L3 = true,
            independentSafetyApprovalRequiredForL2L3 = true,
            permanentProhibitionCount = BoundedAutomationReadinessGovernance.PermanentProhibitions.Count,
            persistence = "SQL Server append-only Evidence"
        });
    }

    [HttpGet("prohibitions")]
    public IActionResult GetPermanentProhibitions()
    {
        if (!TryAllowReadOnly(out _)) return NotFound();
        return Ok(new
        {
            stage = "IDI-P6",
            readOnly = true,
            productionEnablementAllowed = false,
            controlWriteAllowed = false,
            values = BoundedAutomationReadinessGovernance.PermanentProhibitions
                .OrderBy(static x => x)
                .Select(static x => x.ToString())
                .ToArray()
        });
    }

    [HttpGet("evidence")]
    public async Task<IActionResult> ListEvidence([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!TryAllowReadOnly(out _)) return NotFound();
        if (limit is < 1 or > 500) return BadRequest("limit must be in [1,500].");
        try
        {
            var values = await GetFactory().CreateStore().ListAsync(limit, ct);
            return Ok(new
            {
                stage = "IDI-P6",
                readOnly = true,
                finalClaim = BoundedAutomationReadinessGovernance.SoftwareOnlyClaim,
                productionEnablementAllowed = false,
                controlWriteAllowed = false,
                values
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("evidence/{evaluationId}")]
    public async Task<IActionResult> GetEvidence(string evaluationId, CancellationToken ct)
    {
        if (!TryAllowReadOnly(out _)) return NotFound();
        if (!ValidIdentifier(evaluationId))
            return BadRequest("evaluationId is required and must be <= 80 characters.");
        try
        {
            var value = await GetFactory().CreateStore().GetAsync(evaluationId, ct);
            return value is null ? NotFound() : Ok(new
            {
                stage = "IDI-P6",
                readOnly = true,
                finalClaim = BoundedAutomationReadinessGovernance.SoftwareOnlyClaim,
                productionEnablementAllowed = false,
                controlWriteAllowed = false,
                value
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    private BoundedAutomationReadinessPersistenceFactory GetFactory()
    {
        var connectionString = _configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new BoundedAutomationReadinessPersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowReadOnly(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Bounded Automation Readiness read-only operation failed closed",
        detail: ex.Message);

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80;
}
