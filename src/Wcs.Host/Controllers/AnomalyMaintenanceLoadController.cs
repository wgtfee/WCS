namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.Maintenance;
using Wcs.Core.AnomalyDetection.RootCause;

/// <summary>
/// 仅 LoadTest 环境启用，用于验证 v3.7 规则版本、建议幂等、反馈、指标、训练标签和重启恢复。
/// </summary>
[ApiController]
[Route("api/anomaly/maintenance/load")]
public sealed class AnomalyMaintenanceLoadController : ControllerBase
{
    private readonly IAssetHealthMaintenanceDecisionEngine _engine;
    private readonly IAssetHealthMaintenanceStore _store;
    private readonly IHostEnvironment _environment;

    public AnomalyMaintenanceLoadController(
        IAssetHealthMaintenanceDecisionEngine engine,
        IAssetHealthMaintenanceStore store,
        IHostEnvironment environment)
    {
        _engine = engine;
        _store = store;
        _environment = environment;
    }

    [HttpPost("recommendations")]
    public async Task<ActionResult> Generate(
        [FromBody] AssetHealthMaintenanceLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        if (request.Analysis is null || request.HealthEvent is null)
            return BadRequest("analysis and healthEvent are required.");

        await _store.InitializeAsync(cancellationToken);
        await _store.RegisterRuleSetAsync(_engine.RuleSetRegistration, cancellationToken);
        var recommendation = _engine.Generate(request.Analysis, request.HealthEvent, DateTime.UtcNow);
        if (recommendation is null)
            return UnprocessableEntity(
                "No recommendation was generated because the event/root-cause review/rule did not meet approved conditions.");
        var inserted = await _store.SaveRecommendationAsync(recommendation, cancellationToken);
        return Ok(new
        {
            inserted,
            recommendation,
            status = await _store.GetStatusAsync(cancellationToken),
            metrics = await _store.GetMetricsAsync(cancellationToken)
        });
    }

    [HttpPost("recommendations/{recommendationId}/feedback")]
    public async Task<ActionResult> AddFeedback(
        string recommendationId,
        [FromBody] AssetHealthMaintenanceLoadFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        if (string.IsNullOrWhiteSpace(recommendationId) || string.IsNullOrWhiteSpace(request.Actor))
            return BadRequest("recommendationId and actor are required.");
        var existing = await _store.GetRecommendationAsync(recommendationId.Trim(), cancellationToken);
        if (existing is null) return NotFound();
        var occurredAt = request.OccurredAtUtc == default ? DateTime.UtcNow : NormalizeUtc(request.OccurredAtUtc);
        var feedback = new AssetHealthMaintenanceFeedback
        {
            FeedbackId = string.IsNullOrWhiteSpace(request.FeedbackId)
                ? Guid.NewGuid().ToString("N")
                : request.FeedbackId.Trim(),
            RecommendationId = existing.RecommendationId,
            Decision = request.Decision,
            Actor = request.Actor.Trim(),
            OccurredAtUtc = occurredAt,
            PostHealthScore = request.PostHealthScore,
            MesWorkOrderNo = Normalize(request.MesWorkOrderNo),
            AssignedTo = Normalize(request.AssignedTo),
            CompletedAtUtc = request.CompletedAtUtc is null
                ? null
                : NormalizeUtc(request.CompletedAtUtc.Value),
            Note = Normalize(request.Note)
        };
        var result = await _store.AppendFeedbackAsync(feedback, cancellationToken);
        return Ok(new
        {
            recommendation = result,
            feedback = await _store.GetFeedbackAsync(existing.RecommendationId, 100, cancellationToken),
            labels = await _store.GetTrainingLabelCandidatesAsync(null, 100, cancellationToken),
            metrics = await _store.GetMetricsAsync(cancellationToken)
        });
    }

    [HttpPost("maintain")]
    public async Task<ActionResult> Maintain(CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        await _store.MaintainAsync(DateTime.UtcNow, cancellationToken);
        return Ok(await _store.GetStatusAsync(cancellationToken));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class AssetHealthMaintenanceLoadRequest
{
    public AssetHealthRootCauseAnalysisSnapshot? Analysis { get; set; }
    public AssetHealthEventSnapshot? HealthEvent { get; set; }
}

public sealed class AssetHealthMaintenanceLoadFeedbackRequest
{
    public string? FeedbackId { get; set; }
    public MaintenanceFeedbackDecision Decision { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public double? PostHealthScore { get; set; }
    public string? MesWorkOrderNo { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Note { get; set; }
}
