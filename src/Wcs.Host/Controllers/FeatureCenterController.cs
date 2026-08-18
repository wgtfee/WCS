namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.FeatureCenter;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;

[ApiController]
[Route("api/industrial-intelligence")]
public sealed class FeatureCenterController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public FeatureCenterController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("features")]
    public async Task<IActionResult> GetFeatures(CancellationToken ct)
    {
        if (!TryAllowP2(out _)) return NotFound();
        try
        {
            var values = await GetFactory().CreateDefinitionRegistry().ListAsync(ct);
            return Ok(new { stage = "IDI-P2", controlWriteAllowed = false, values });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpGet("feature-schemas/{schemaId}/{version}")]
    public async Task<IActionResult> GetSchema(string schemaId, string version, CancellationToken ct)
    {
        if (!TryAllowP2(out _)) return NotFound();
        if (!ValidId(schemaId) || !ValidId(version)) return BadRequest("schemaId/version is invalid.");
        try
        {
            var value = await GetFactory().CreateSchemaRegistry().GetAsync(schemaId, version, ct);
            return value is null ? NotFound() : Ok(new { stage = "IDI-P2", controlWriteAllowed = false, value });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpGet("feature-snapshots/{snapshotId}")]
    public async Task<IActionResult> GetSnapshot(string snapshotId, CancellationToken ct)
    {
        if (!TryAllowP2(out _)) return NotFound();
        if (!ValidId(snapshotId)) return BadRequest("snapshotId is invalid.");
        try
        {
            var value = await GetFactory().CreateSnapshotStore().GetAsync(snapshotId, ct);
            return value is null ? NotFound() : Ok(new { stage = "IDI-P2", replayable = true, controlWriteAllowed = false, value });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpGet("datasets/{datasetId}/{version}")]
    public async Task<IActionResult> GetDataset(string datasetId, string version, CancellationToken ct)
    {
        if (!TryAllowP2(out _)) return NotFound();
        if (!ValidId(datasetId) || !ValidId(version)) return BadRequest("datasetId/version is invalid.");
        try
        {
            var value = await GetFactory().CreateDatasetStore().GetAsync(datasetId, version, ct);
            return value is null ? NotFound() : Ok(new { stage = "IDI-P2", pointInTime = true, controlWriteAllowed = false, value });
        }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    [HttpPost("feature-schemas")]
    public async Task<IActionResult> RegisterSchema([FromBody] FeatureSchemaRegistration command, CancellationToken ct)
    {
        if (!TryAllowP2(out _)) return NotFound();
        if (command?.Schema is null || !ValidActor(command.Actor) || !ValidReason(command.Reason) || !ValidId(command.CorrelationId))
            return BadRequest("schema, actor, reason and correlationId are required and bounded.");
        if (!string.Equals(FeatureSchemaHash.Compute(command.Schema), command.Schema.SchemaHash, StringComparison.OrdinalIgnoreCase))
            return BadRequest("FeatureSchema hash is invalid.");
        try
        {
            await GetFactory().CreateSchemaRegistry().RegisterAsync(command.Schema, ct);
            return Ok(new { registered = true, immutableVersion = true, controlWriteAllowed = false, command.Schema.SchemaId, command.Schema.Version, command.Schema.SchemaHash });
        }
        catch (InvalidOperationException ex) { return Conflict(new { failClosed = true, error = ex.Message }); }
        catch (Exception ex) { return PersistenceProblem(ex); }
    }

    private FeatureCenterPersistenceFactory GetFactory()
    {
        var cs = _configuration.GetConnectionString("WcsDb") ?? throw new InvalidOperationException("ConnectionStrings:WcsDb is not configured.");
        var factory = new FeatureCenterPersistenceFactory(cs);
        factory.EnsureSchema();
        return factory;
    }

    private bool TryAllowP2(out IndustrialIntelligenceAccessDecision decision)
    {
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
        return decision.Allowed && decision.EffectiveMaximumAutomationLevel <= AutomationLevel.L1;
    }

    private IActionResult PersistenceProblem(Exception ex) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Feature Center operation failed closed",
        detail: ex.Message);

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160;
    private static bool ValidActor(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static bool ValidReason(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2000;
}

public sealed class FeatureSchemaRegistration
{
    public FeatureSchema? Schema { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}
