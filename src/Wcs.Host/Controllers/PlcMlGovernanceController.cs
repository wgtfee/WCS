namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/ml/governance")]
public sealed class PlcMlGovernanceController : ControllerBase
{
    private readonly IPlcMlGovernanceService _service;
    private readonly PlcMlAnomalyOptions _options;

    public PlcMlGovernanceController(
        IPlcMlGovernanceService service,
        PlcMlAnomalyOptions options)
    {
        _service = service;
        _options = options;
    }

    [HttpPost("datasets/{profileId}")]
    public async Task<ActionResult<PlcMlDatasetInfo>> CreateDataset(
        string profileId,
        [FromBody] PlcMlDatasetRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.CreateDatasetAsync(
            profileId,
            request.CreatedBy,
            request.Description,
            cancellationToken));
    }

    [HttpGet("datasets/{profileId}")]
    public async Task<ActionResult<IReadOnlyList<PlcMlDatasetInfo>>> ListDatasets(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.ListDatasetsAsync(profileId, cancellationToken));
    }

    [HttpGet("candidates")]
    public async Task<ActionResult<IReadOnlyList<PlcMlCandidateRecord>>> QueryCandidates(
        [FromQuery] string? profileId,
        [FromQuery] PlcMlReviewDecision? decision,
        [FromQuery] int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.QueryCandidatesAsync(
            profileId,
            decision,
            maximumCount,
            cancellationToken));
    }

    [HttpPost("candidates/{candidateId}/review")]
    public async Task<ActionResult<PlcMlCandidateRecord>> ReviewCandidate(
        string candidateId,
        [FromBody] PlcMlCandidateReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.ReviewCandidateAsync(
            candidateId,
            request.Decision,
            request.ReviewedBy,
            request.Comment,
            cancellationToken));
    }

    [HttpGet("models/{profileId}")]
    public async Task<ActionResult<IReadOnlyList<PlcMlModelGovernanceInfo>>> ListModelGovernance(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.ListModelGovernanceAsync(profileId, cancellationToken));
    }

    [HttpPost("models/{profileId}/{version}/approve")]
    public async Task<ActionResult<PlcMlModelGovernanceInfo>> ApproveModel(
        string profileId,
        string version,
        [FromBody] PlcMlModelDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.ApproveModelAsync(
            profileId,
            version,
            request.Actor,
            request.Comment,
            request.Activate,
            cancellationToken));
    }

    [HttpPost("models/{profileId}/{version}/reject")]
    public async Task<ActionResult<PlcMlModelGovernanceInfo>> RejectModel(
        string profileId,
        string version,
        [FromBody] PlcMlModelDecisionRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.RejectModelAsync(
            profileId,
            version,
            request.Actor,
            request.Comment,
            cancellationToken));
    }

    [HttpGet("evaluation/{profileId}")]
    public async Task<ActionResult<PlcMlEvaluationSummary>> GetEvaluation(
        string profileId,
        [FromQuery] string? modelVersion,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.GetEvaluationAsync(
            profileId,
            modelVersion,
            cancellationToken));
    }

    [HttpGet("drift/{profileId}")]
    public async Task<ActionResult<PlcMlDriftSnapshot?>> GetDrift(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _service.GetLatestDriftAsync(profileId, cancellationToken));
    }

    private static async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new OkObjectResult(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(new { error = ex.Message });
        }
    }
}

public sealed class PlcMlDatasetRequest
{
    public string CreatedBy { get; set; } = "system";
    public string? Description { get; set; }
}

public sealed class PlcMlCandidateReviewRequest
{
    public PlcMlReviewDecision Decision { get; set; }
    public string ReviewedBy { get; set; } = string.Empty;
    public string? Comment { get; set; }
}

public sealed class PlcMlModelDecisionRequest
{
    public string Actor { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public bool Activate { get; set; }
}
