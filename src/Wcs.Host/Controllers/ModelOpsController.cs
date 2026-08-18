namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;
using Wcs.ModelOps;

[ApiController]
[Route("api/modelops")]
public sealed class ModelOpsController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public ModelOpsController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!TryAllowP1(out var access))
            return NotFound();

        try
        {
            var factory = GetFactory();
            var recovery = await factory.CreateRecoveryService().ValidateAsync(ct);
            return Ok(new
            {
                stage = "IDI-P1",
                environment = _environment.EnvironmentName,
                mode = access.EffectiveMode.ToString(),
                maximumAutomationLevel = access.EffectiveMaximumAutomationLevel.ToString(),
                controlWriteAllowed = false,
                autoPromotionAllowed = false,
                productionAutomationAllowed = false,
                persistence = "SQL Server",
                recoveryHealthy = recovery.IsHealthy,
                recoveryErrors = recovery.Errors,
                recoveryScopeCount = recovery.ScopeCount,
                championCount = recovery.ChampionCount,
                fallbackCount = recovery.FallbackCount,
                shadowCount = recovery.ShadowCount,
                quarantinedCount = recovery.QuarantinedCount
            });
        }
        catch (Exception ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "ModelOps persistence unavailable",
                detail: ex.Message);
        }
    }

    [HttpGet("registry/{modelId}")]
    public async Task<IActionResult> GetRegistry(string modelId, CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        if (!ValidIdentifier(modelId))
            return BadRequest("modelId is required and must be <= 120 characters.");

        try
        {
            var factory = GetFactory();
            var versions = await factory.CreateRegistry().ListAsync(modelId, ct);
            return Ok(new
            {
                stage = "IDI-P1",
                controlWriteAllowed = false,
                modelId,
                versions
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("deployments")]
    public async Task<IActionResult> GetDeployments(
        [FromQuery] string modelId,
        [FromQuery] string assetType,
        [FromQuery] string profile,
        CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        if (!ValidIdentifier(modelId) || !ValidIdentifier(assetType) || !ValidIdentifier(profile))
            return BadRequest("modelId, assetType and profile are required and must be <= 120 characters.");

        try
        {
            var factory = GetFactory();
            var deployments = await factory.CreateDeploymentStore()
                .ListScopeAsync(modelId, assetType, profile, ct);
            ModelDeploymentInvariants.ThrowIfInvalid(deployments);
            return Ok(new
            {
                stage = "IDI-P1",
                controlWriteAllowed = false,
                deployments
            });
        }
        catch (ModelDeploymentInvariantException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] string? modelId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        if (!string.IsNullOrWhiteSpace(modelId) && !ValidIdentifier(modelId))
            return BadRequest("modelId must be <= 120 characters.");
        if (limit is < 1 or > 500)
            return BadRequest("limit must be in [1,500].");

        try
        {
            var factory = GetFactory();
            var entries = await factory.CreateAuditJournal().ListAsync(modelId, limit, ct);
            return Ok(new
            {
                stage = "IDI-P1",
                appendOnly = true,
                controlWriteAllowed = false,
                entries
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("evaluations/{modelId}")]
    public async Task<IActionResult> GetEvaluations(string modelId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        if (!ValidIdentifier(modelId) || limit is < 1 or > 500)
            return BadRequest("modelId is required and limit must be in [1,500].");

        try
        {
            var factory = GetFactory();
            var values = await factory.CreateEvaluationStore().ListAsync(modelId, limit, ct);
            return Ok(new { stage = "IDI-P1", autoPromotionAllowed = false, values });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("drift/{modelId}")]
    public async Task<IActionResult> GetDrift(string modelId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        if (!ValidIdentifier(modelId) || limit is < 1 or > 500)
            return BadRequest("modelId is required and limit must be in [1,500].");

        try
        {
            var factory = GetFactory();
            var values = await factory.CreateDriftStore().ListAsync(modelId, limit, ct);
            return Ok(new { stage = "IDI-P1", autoQuarantineAllowed = false, values });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("registry")]
    public async Task<IActionResult> Register([FromBody] ModelRegistrationCommand command, CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        var validation = ValidateRegistration(command);
        if (validation is not null)
            return BadRequest(validation);

        try
        {
            var factory = GetFactory();
            var version = new AiModelVersion(
                command.Manifest!,
                AiModelLifecycleStatus.Candidate,
                DateTimeOffset.UtcNow,
                command.Actor.Trim(),
                command.CorrelationId.Trim());
            await factory.CreateRegistry().RegisterAsync(version, ct);
            return Ok(new
            {
                registered = true,
                immutableVersion = true,
                controlWriteAllowed = false,
                version.ModelId,
                version.Version,
                version.Manifest.ManifestHash
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("deployments/shadow")]
    public Task<IActionResult> PromoteShadow([FromBody] ModelDeploymentRequest request, CancellationToken ct) =>
        ExecuteDeploymentAsync(
            request,
            (manager, token) => manager.PromoteToShadowAsync(request, token),
            "Shadow",
            ct);

    [HttpPost("deployments/champion")]
    public Task<IActionResult> PromoteChampion([FromBody] ModelDeploymentRequest request, CancellationToken ct) =>
        ExecuteDeploymentAsync(
            request,
            (manager, token) => manager.PromoteToChampionAsync(request, token),
            "Champion",
            ct);

    [HttpPost("deployments/rollback")]
    public async Task<IActionResult> Rollback([FromBody] ModelRollbackRequest request, CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        var error = ValidateRollback(request);
        if (error is not null)
            return BadRequest(error);

        try
        {
            var factory = GetFactory();
            var manager = factory.CreateDeploymentManager();
            await manager.RollbackAsync(request, ct);
            return Ok(new { accepted = true, action = "Rollback", controlWriteAllowed = false, autoControl = false });
        }
        catch (ModelDeploymentInvariantException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("deployments/quarantine")]
    public async Task<IActionResult> Quarantine([FromBody] ModelQuarantineRequest request, CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        var error = ValidateQuarantine(request);
        if (error is not null)
            return BadRequest(error);

        try
        {
            var factory = GetFactory();
            var manager = factory.CreateDeploymentManager();
            await manager.QuarantineAsync(request, ct);
            return Ok(new
            {
                accepted = true,
                action = "Quarantine",
                failClosedIfChampion = true,
                autoFallbackPromotion = false,
                controlWriteAllowed = false
            });
        }
        catch (ModelDeploymentInvariantException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    private async Task<IActionResult> ExecuteDeploymentAsync(
        ModelDeploymentRequest request,
        Func<IModelDeploymentGovernanceManager, CancellationToken, Task> operation,
        string action,
        CancellationToken ct)
    {
        if (!TryAllowP1(out _))
            return NotFound();
        var error = ValidateDeployment(request);
        if (error is not null)
            return BadRequest(error);

        try
        {
            var factory = GetFactory();
            var manager = factory.CreateDeploymentManager();
            await operation(manager, ct);
            return Ok(new { accepted = true, action, controlWriteAllowed = false, autoControl = false });
        }
        catch (ModelDeploymentInvariantException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { failClosed = true, error = ex.Message });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    private ModelOpsPersistenceFactory GetFactory()
    {
        var connectionString = _configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new ModelOpsPersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowP1(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "ModelOps operation failed closed",
        detail: ex.Message);

    private static string? ValidateRegistration(ModelRegistrationCommand? command)
    {
        if (command?.Manifest is null)
            return "manifest is required.";
        if (!ValidActor(command.Actor) || !ValidReason(command.Reason) || !ValidCorrelation(command.CorrelationId))
            return "actor, reason or correlationId is invalid.";
        var errors = ModelOpsContractRules.ValidateManifest(command.Manifest);
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static string? ValidateDeployment(ModelDeploymentRequest? request)
    {
        if (request is null)
            return "request is required.";
        if (!ValidIdentifier(request.ModelId) || !ValidIdentifier(request.Version) ||
            !ValidIdentifier(request.AssetType) || !ValidIdentifier(request.Profile) ||
            !ValidActor(request.Actor) || !ValidReason(request.Reason) || !ValidCorrelation(request.CorrelationId))
            return "deployment request contains missing or oversized fields.";
        return null;
    }

    private static string? ValidateRollback(ModelRollbackRequest? request)
    {
        if (request is null)
            return "request is required.";
        if (!ValidIdentifier(request.ModelId) || !ValidIdentifier(request.AssetType) ||
            !ValidIdentifier(request.Profile) || !ValidActor(request.Actor) ||
            !ValidReason(request.Reason) || !ValidCorrelation(request.CorrelationId))
            return "rollback request contains missing or oversized fields.";
        return null;
    }

    private static string? ValidateQuarantine(ModelQuarantineRequest? request)
    {
        if (request is null)
            return "request is required.";
        if (!ValidIdentifier(request.ModelId) || !ValidIdentifier(request.Version) ||
            !ValidIdentifier(request.AssetType) || !ValidIdentifier(request.Profile) ||
            !ValidActor(request.Actor) || !ValidReason(request.Reason) || !ValidCorrelation(request.CorrelationId))
            return "quarantine request contains missing or oversized fields.";
        return null;
    }

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120;
    private static bool ValidActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static bool ValidReason(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2000;
    private static bool ValidCorrelation(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120;
}

public sealed class ModelRegistrationCommand
{
    public AiModelPackageManifest? Manifest { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}
