namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.RootCause;

[ApiController]
[Route("api/anomaly/root-cause")]
public sealed class AnomalyRootCauseController : ControllerBase
{
    private readonly AssetHealthRootCauseOptions _options;
    private readonly IAssetHealthRootCauseAnalysisEngine _engine;
    private readonly IAssetHealthRootCauseRuntimeStatus _runtime;
    private readonly IAssetHealthRootCauseAnalysisStore _store;

    public AnomalyRootCauseController(
        AssetHealthRootCauseOptions options,
        IAssetHealthRootCauseAnalysisEngine engine,
        IAssetHealthRootCauseRuntimeStatus runtime,
        IAssetHealthRootCauseAnalysisStore store)
    {
        _options = options;
        _engine = engine;
        _runtime = runtime;
        _store = store;
    }

    [HttpGet("status")]
    public async Task<ActionResult<AssetHealthRootCauseRuntimeStatus>> GetStatus(
        CancellationToken cancellationToken) => Ok(new AssetHealthRootCauseRuntimeStatus
    {
        Runtime = _runtime.GetStatus(),
        Store = await _store.GetStatusAsync(cancellationToken)
    });

    [HttpGet("graph")]
    public ActionResult<AssetHealthRootCauseGraphResponse> GetGraph() => Ok(new AssetHealthRootCauseGraphResponse
    {
        Registration = _engine.GraphRegistration,
        AllowCycles = _options.AllowCycles,
        MaximumPropagationDepth = _options.MaximumPropagationDepth,
        Nodes = _options.Graph.Nodes,
        Edges = _options.Graph.Edges
    });

    [HttpGet("analyses")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthRootCauseAnalysisSnapshot>>> GetAnalyses(
        [FromQuery] string? triggerEventId = null,
        [FromQuery] RootCauseReviewDecision? reviewDecision = null,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) => Ok(await _store.GetAnalysesAsync(
            triggerEventId,
            reviewDecision,
            Math.Clamp(maxCount, 1, 10_000),
            cancellationToken));

    [HttpGet("analyses/{analysisId}")]
    public async Task<ActionResult<AssetHealthRootCauseAnalysisSnapshot>> GetAnalysis(
        string analysisId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) return BadRequest();
        var result = await _store.GetAsync(analysisId.Trim(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("events/{eventId}/latest")]
    public async Task<ActionResult<AssetHealthRootCauseAnalysisSnapshot>> GetLatestForEvent(
        string eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return BadRequest();
        var result = await _store.GetLatestForTriggerAsync(eventId.Trim(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("analyses/{analysisId}/reviews")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthRootCauseReview>>> GetReviews(
        string analysisId,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(analysisId)) return BadRequest();
        return Ok(await _store.GetReviewsAsync(
            analysisId.Trim(),
            Math.Clamp(maxCount, 1, 10_000),
            cancellationToken));
    }

    [HttpPost("analyses/{analysisId}/reviews")]
    public async Task<ActionResult<AssetHealthRootCauseAnalysisSnapshot>> Review(
        string analysisId,
        [FromBody] AssetHealthRootCauseReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisId) || request.Decision == RootCauseReviewDecision.Pending)
            return BadRequest("analysisId and a non-pending decision are required.");
        var actor = ResolveActor(request.Actor);
        if (actor is null) return BadRequest("actor is required.");
        var analysis = await _store.GetAsync(analysisId.Trim(), cancellationToken);
        if (analysis is null) return NotFound();

        string? selectedNodeId = string.IsNullOrWhiteSpace(request.SelectedRootCauseNodeId)
            ? null
            : request.SelectedRootCauseNodeId.Trim();
        if (request.Decision == RootCauseReviewDecision.Confirmed)
        {
            selectedNodeId ??= analysis.PrimaryCandidate?.NodeId;
            if (selectedNodeId is null || analysis.Candidates.All(candidate => candidate.NodeId != selectedNodeId))
                return BadRequest("Confirmed review must select one of the inferred candidates.");
        }
        else if (request.Decision == RootCauseReviewDecision.Rejected)
        {
            selectedNodeId = null;
        }
        else if (request.Decision == RootCauseReviewDecision.Supplemented)
        {
            if (selectedNodeId is null || _options.Graph.Nodes.All(node => node.NodeId != selectedNodeId))
                return BadRequest("Supplemented review must select a node from the approved graph.");
            if (string.IsNullOrWhiteSpace(request.Note))
                return BadRequest("Supplemented review requires an explanatory note.");
        }

        var review = new AssetHealthRootCauseReview
        {
            ReviewId = Guid.NewGuid().ToString("N"),
            AnalysisId = analysis.AnalysisId,
            Decision = request.Decision,
            SelectedRootCauseNodeId = selectedNodeId,
            Actor = actor,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            OccurredAtUtc = DateTime.UtcNow
        };
        var result = await _store.AppendReviewAsync(review, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    private string? ResolveActor(string? requestedActor)
    {
        var identity = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        if (!string.IsNullOrWhiteSpace(identity)) return identity.Trim();
        return string.IsNullOrWhiteSpace(requestedActor) ? null : requestedActor.Trim();
    }
}

public sealed record AssetHealthRootCauseRuntimeStatus
{
    public required AssetHealthRootCauseStatus Runtime { get; init; }
    public required AssetHealthRootCauseStoreStatus Store { get; init; }
}

public sealed record AssetHealthRootCauseGraphResponse
{
    public required RootCauseGraphRegistration Registration { get; init; }
    public required bool AllowCycles { get; init; }
    public required int MaximumPropagationDepth { get; init; }
    public required IReadOnlyList<RootCauseGraphNode> Nodes { get; init; }
    public required IReadOnlyList<RootCauseGraphEdge> Edges { get; init; }
}

public sealed class AssetHealthRootCauseReviewRequest
{
    public RootCauseReviewDecision Decision { get; set; }
    public string? SelectedRootCauseNodeId { get; set; }
    public string? Actor { get; set; }
    public string? Note { get; set; }
}
