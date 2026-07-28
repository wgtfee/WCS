namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Maintenance;

[ApiController]
[Route("api/anomaly/maintenance")]
public sealed class AnomalyMaintenanceController : ControllerBase
{
    private readonly AssetHealthMaintenanceOptions _options;
    private readonly IAssetHealthMaintenanceDecisionEngine _engine;
    private readonly IAssetHealthMaintenanceRuntimeStatus _runtime;
    private readonly IAssetHealthMaintenanceStore _store;

    public AnomalyMaintenanceController(
        AssetHealthMaintenanceOptions options,
        IAssetHealthMaintenanceDecisionEngine engine,
        IAssetHealthMaintenanceRuntimeStatus runtime,
        IAssetHealthMaintenanceStore store)
    {
        _options = options;
        _engine = engine;
        _runtime = runtime;
        _store = store;
    }

    [HttpGet("status")]
    public async Task<ActionResult<AssetHealthMaintenanceRuntimeResponse>> GetStatus(
        CancellationToken cancellationToken) => Ok(new AssetHealthMaintenanceRuntimeResponse
    {
        Runtime = _runtime.GetStatus(),
        Store = await _store.GetStatusAsync(cancellationToken),
        Metrics = await _store.GetMetricsAsync(cancellationToken)
    });

    [HttpGet("rules")]
    public ActionResult<AssetHealthMaintenanceRulesResponse> GetRules() => Ok(
        new AssetHealthMaintenanceRulesResponse
        {
            Registration = _engine.RuleSetRegistration,
            MinimumRootCauseConfidence = _options.MinimumRootCauseConfidence,
            Rules = _options.RuleSet.Rules
        });

    [HttpGet("recommendations")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthMaintenanceRecommendation>>> GetRecommendations(
        [FromQuery] MaintenanceRecommendationStatus? status = null,
        [FromQuery] string? assetId = null,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) => Ok(await _store.GetRecommendationsAsync(
            status,
            assetId,
            Math.Clamp(maxCount, 1, _options.MaximumRecommendationsQueryCount),
            cancellationToken));

    [HttpGet("recommendations/{recommendationId}")]
    public async Task<ActionResult<AssetHealthMaintenanceRecommendation>> GetRecommendation(
        string recommendationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recommendationId)) return BadRequest();
        var result = await _store.GetRecommendationAsync(recommendationId.Trim(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("analyses/{analysisId}/latest")]
    public async Task<ActionResult<AssetHealthMaintenanceRecommendation>> GetLatestForAnalysis(
        string analysisId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) return BadRequest();
        var result = await _store.GetLatestForAnalysisAsync(analysisId.Trim(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("recommendations/{recommendationId}/feedback")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthMaintenanceFeedback>>> GetFeedback(
        string recommendationId,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recommendationId)) return BadRequest();
        return Ok(await _store.GetFeedbackAsync(
            recommendationId.Trim(),
            Math.Clamp(maxCount, 1, _options.MaximumRecommendationsQueryCount),
            cancellationToken));
    }

    [HttpPost("recommendations/{recommendationId}/feedback")]
    public async Task<ActionResult<AssetHealthMaintenanceRecommendation>> AddFeedback(
        string recommendationId,
        [FromBody] AssetHealthMaintenanceFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recommendationId)) return BadRequest("recommendationId is required.");
        var actor = ResolveActor(request.Actor);
        if (actor is null) return BadRequest("actor is required.");
        if (!Enum.IsDefined(request.Decision)) return BadRequest("decision is invalid.");
        if (request.PostHealthScore is < 0 or > 100)
            return BadRequest("postHealthScore must be between 0 and 100.");
        if (request.Decision is (MaintenanceFeedbackDecision.Rejected or
                MaintenanceFeedbackDecision.FalsePositive or
                MaintenanceFeedbackDecision.NoFaultFound) &&
            string.IsNullOrWhiteSpace(request.Note))
            return BadRequest("Rejected, FalsePositive and NoFaultFound feedback require a note.");

        var existing = await _store.GetRecommendationAsync(recommendationId.Trim(), cancellationToken);
        if (existing is null) return NotFound();
        var occurredAt = DateTime.UtcNow;
        var completedAt = request.CompletedAtUtc;
        if (request.Decision is MaintenanceFeedbackDecision.Repaired or
            MaintenanceFeedbackDecision.FalsePositive or
            MaintenanceFeedbackDecision.NoFaultFound)
            completedAt ??= occurredAt;

        var feedback = new AssetHealthMaintenanceFeedback
        {
            FeedbackId = Guid.NewGuid().ToString("N"),
            RecommendationId = existing.RecommendationId,
            Decision = request.Decision,
            Actor = actor,
            OccurredAtUtc = occurredAt,
            PostHealthScore = request.PostHealthScore,
            MesWorkOrderNo = Normalize(request.MesWorkOrderNo),
            AssignedTo = Normalize(request.AssignedTo),
            CompletedAtUtc = completedAt,
            Note = Normalize(request.Note)
        };
        var result = await _store.AppendFeedbackAsync(feedback, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("training-labels")]
    public async Task<ActionResult<IReadOnlyList<MaintenanceTrainingLabelCandidate>>> GetTrainingLabels(
        [FromQuery] MaintenanceTrainingLabelStatus? status = null,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) => Ok(
            await _store.GetTrainingLabelCandidatesAsync(
                status,
                Math.Clamp(maxCount, 1, _options.MaximumRecommendationsQueryCount),
                cancellationToken));

    [HttpPost("training-labels/{candidateId}/review")]
    public async Task<ActionResult<MaintenanceTrainingLabelCandidate>> ReviewTrainingLabel(
        string candidateId,
        [FromBody] MaintenanceTrainingLabelReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateId)) return BadRequest("candidateId is required.");
        if (request.Status == MaintenanceTrainingLabelStatus.PendingApproval)
            return BadRequest("status must be Approved or Rejected.");
        var actor = ResolveActor(request.Actor);
        if (actor is null) return BadRequest("actor is required.");
        var result = await _store.ReviewTrainingLabelAsync(
            candidateId.Trim(),
            request.Status,
            actor,
            Normalize(request.Note),
            DateTime.UtcNow,
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    private string? ResolveActor(string? requestedActor)
    {
        var identity = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        if (!string.IsNullOrWhiteSpace(identity)) return identity.Trim();
        return Normalize(requestedActor);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AssetHealthMaintenanceRuntimeResponse
{
    public required AssetHealthMaintenanceStatus Runtime { get; init; }
    public required AssetHealthMaintenanceStoreStatus Store { get; init; }
    public required AssetHealthMaintenanceMetrics Metrics { get; init; }
}

public sealed record AssetHealthMaintenanceRulesResponse
{
    public required MaintenanceRuleSetRegistration Registration { get; init; }
    public required double MinimumRootCauseConfidence { get; init; }
    public required IReadOnlyList<MaintenanceDecisionRule> Rules { get; init; }
}

public sealed class AssetHealthMaintenanceFeedbackRequest
{
    public MaintenanceFeedbackDecision Decision { get; set; }
    public string? Actor { get; set; }
    public double? PostHealthScore { get; set; }
    public string? MesWorkOrderNo { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Note { get; set; }
}

public sealed class MaintenanceTrainingLabelReviewRequest
{
    public MaintenanceTrainingLabelStatus Status { get; set; }
    public string? Actor { get; set; }
    public string? Note { get; set; }
}
