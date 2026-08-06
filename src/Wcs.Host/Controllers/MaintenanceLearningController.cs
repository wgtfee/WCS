namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;
using Wcs.MaintenanceLearning;

[ApiController]
[Route("api/maintenance-learning")]
public sealed class MaintenanceLearningController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public MaintenanceLearningController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!TryAllowP4(out var access)) return NotFound();
        try
        {
            var factory = GetFactory();
            var recovery = await factory.CreateRecovery().RecoverAsync(ct);
            return Ok(new
            {
                stage = "IDI-P4",
                environment = _environment.EnvironmentName,
                mode = access.EffectiveMode.ToString(),
                maximumAutomationLevel = access.EffectiveMaximumAutomationLevel.ToString(),
                controlWriteAllowed = false,
                autoTrainingAllowed = false,
                autoModelActivationAllowed = false,
                productionAutomationAllowed = false,
                persistence = "SQL Server",
                recovery
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("interventions/{interventionId}")]
    public async Task<IActionResult> GetIntervention(string interventionId, CancellationToken ct)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (!ValidIdentifier(interventionId)) return BadRequest("interventionId is required and must be <= 120 characters.");
        try
        {
            var value = await GetFactory().CreateStore().GetInterventionAsync(interventionId, ct);
            return value is null ? NotFound() : Ok(new { stage = "IDI-P4", controlWriteAllowed = false, value });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpGet("outbox/pending")]
    public async Task<IActionResult> GetPendingOutbox([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (limit is < 1 or > 500) return BadRequest("limit must be in [1,500].");
        try
        {
            var values = await GetFactory().CreateStore().LoadPendingOutboxAsync(limit, ct);
            return Ok(new { stage = "IDI-P4", retryOnly = true, controlWriteAllowed = false, values });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("interventions")]
    public async Task<IActionResult> RecordIntervention([FromBody] MaintenanceIntervention command, CancellationToken ct)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (command is null || !ValidIdentifier(command.InterventionId) || !ValidIdentifier(command.AssetId) ||
            !ValidIdentifier(command.AssetType) || !ValidActor(command.Actor) || !ValidCorrelation(command.CorrelationId) ||
            string.IsNullOrWhiteSpace(command.PreFeatureSnapshotId) || command.Cost < 0 || command.CompletedAt < command.StartedAt)
            return BadRequest("invalid intervention command.");
        try
        {
            await GetFactory().CreateStore().SaveInterventionAsync(command, ct);
            return Ok(new { recorded = true, controlWriteAllowed = false, autoTrainingAllowed = false, command.InterventionId });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("outcomes")]
    public async Task<IActionResult> RecordOutcome([FromBody] MaintenanceOutcome command, CancellationToken ct)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (command is null || !ValidIdentifier(command.OutcomeId) || !ValidIdentifier(command.InterventionId) ||
            string.IsNullOrWhiteSpace(command.SourceEventId) || command.DowntimeMinutes < 0 || command.ActualCost < 0)
            return BadRequest("invalid outcome command.");
        try
        {
            await GetFactory().CreateStore().SaveOutcomeAsync(command, ct);
            return Ok(new { recorded = true, idempotentBySourceEvent = true, controlWriteAllowed = false, command.OutcomeId });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("labels")]
    public async Task<IActionResult> RecordLabel([FromBody] TrainingLabelCandidate command, CancellationToken ct)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (command is null || !ValidIdentifier(command.LabelId) || !ValidIdentifier(command.InterventionId) ||
            command.State != TrainingLabelApprovalState.Pending || !ValidHash(command.EvidenceHash) || string.IsNullOrWhiteSpace(command.DatasetKey))
            return BadRequest("new label candidates must be valid and Pending.");
        try
        {
            await GetFactory().CreateStore().SaveLabelAsync(command, ct);
            return Ok(new { recorded = true, datasetAdmissionAllowed = false, autoTrainingAllowed = false, command.LabelId });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    [HttpPost("labels/{labelId}/decision")]
    public async Task<IActionResult> DecideLabel(string labelId, [FromBody] MaintenanceLabelDecisionCommand command, CancellationToken ct)
    {
        if (!TryAllowP4(out _)) return NotFound();
        if (!ValidIdentifier(labelId) || command is null || command.State == TrainingLabelApprovalState.Pending ||
            !ValidActor(command.Actor) || !ValidReason(command.Reason) || !ValidCorrelation(command.CorrelationId) ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 160)
            return BadRequest("invalid label decision.");
        try
        {
            var approval = new TrainingLabelApproval(labelId, command.State, command.Actor.Trim(), command.Reason.Trim(), DateTimeOffset.UtcNow);
            await GetFactory().CreateStore().SaveApprovalAsync(approval, command.CorrelationId.Trim(), command.IdempotencyKey.Trim(), ct);
            return Ok(new
            {
                recorded = true,
                state = command.State.ToString(),
                explicitHumanDecision = true,
                autoTrainingAllowed = false,
                autoModelActivationAllowed = false,
                controlWriteAllowed = false
            });
        }
        catch (Exception ex)
        {
            return PersistenceProblem(ex);
        }
    }

    private MaintenanceLearningPersistenceFactory GetFactory()
    {
        var connectionString = _configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new MaintenanceLearningPersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowP4(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Maintenance Learning operation failed closed",
        detail: ex.Message);

    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120;
    private static bool ValidActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static bool ValidReason(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2000;
    private static bool ValidCorrelation(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120;
    private static bool ValidHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed class MaintenanceLabelDecisionCommand
{
    public TrainingLabelApprovalState State { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
}
