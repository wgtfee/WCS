namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/forecast")]
public sealed class AnomalyForecastController : ControllerBase
{
    private readonly AssetFailureForecastOptions _options;
    private readonly PlcMlAnomalyOptions _mlOptions;
    private readonly IAssetFailureForecastService _service;

    public AnomalyForecastController(
        AssetFailureForecastOptions options,
        PlcMlAnomalyOptions mlOptions,
        IAssetFailureForecastService service)
    {
        _options = options;
        _mlOptions = mlOptions;
        _service = service;
    }

    [HttpGet("status")]
    public ActionResult<AssetFailureForecastStatus> GetStatus() => Ok(_service.GetStatus());

    [HttpGet("models")]
    public async Task<ActionResult<IReadOnlyList<AssetFailureForecastModelManifest>>> GetModels(
        CancellationToken cancellationToken) => Ok(await _service.ListModelsAsync(cancellationToken));

    [HttpGet("forecasts")]
    public async Task<ActionResult<IReadOnlyList<AssetFailureForecastPrediction>>> GetForecasts(
        [FromQuery] string? assetId = null,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) => Ok(await _service.QueryAsync(
            assetId,
            Math.Clamp(maxCount, 1, _options.MaximumForecastsQueryCount),
            cancellationToken));

    [HttpGet("assets/{assetId}/latest")]
    public async Task<ActionResult<AssetFailureForecastPrediction>> GetLatest(
        string assetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest("assetId is required.");
        var forecast = await _service.GetLatestAsync(assetId.Trim(), cancellationToken);
        return forecast is null ? NotFound() : Ok(forecast);
    }

    [HttpGet("metrics")]
    public async Task<ActionResult<AssetFailureForecastMetrics>> GetMetrics(
        CancellationToken cancellationToken) => Ok(await _service.GetMetricsAsync(cancellationToken));

    [HttpGet("forecasts/{forecastId}/outcomes")]
    public async Task<ActionResult<IReadOnlyList<AssetFailureForecastOutcome>>> GetOutcomes(
        string forecastId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(forecastId)) return BadRequest("forecastId is required.");
        return Ok(await _service.GetOutcomesAsync(forecastId.Trim(), cancellationToken));
    }

    [HttpPost("assets/{assetId}/evaluate")]
    public async Task<ActionResult<AssetFailureForecastAttempt>> Evaluate(
        string assetId,
        CancellationToken cancellationToken)
    {
        if (!_mlOptions.ManagementApiEnabled) return NotFound();
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest("assetId is required.");
        var result = await _service.EvaluateAssetAsync(assetId.Trim(), DateTime.UtcNow, cancellationToken);
        return result.Availability switch
        {
            AssetFailureForecastAvailability.Ready => Ok(result),
            AssetFailureForecastAvailability.InsufficientData => UnprocessableEntity(result),
            AssetFailureForecastAvailability.Disabled or AssetFailureForecastAvailability.ModelUnavailable => Conflict(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result)
        };
    }

    [HttpPost("models/{version}/activate")]
    public async Task<ActionResult> ActivateModel(
        string version,
        CancellationToken cancellationToken)
    {
        if (!_mlOptions.ManagementApiEnabled) return NotFound();
        if (string.IsNullOrWhiteSpace(version)) return BadRequest("version is required.");
        try
        {
            await _service.ActivateModelAsync(version.Trim(), cancellationToken);
            return Ok(_service.GetStatus());
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    [HttpPost("forecasts/{forecastId}/outcomes")]
    public async Task<ActionResult<AssetFailureForecastOutcome>> AddOutcome(
        string forecastId,
        [FromBody] AssetFailureForecastOutcomeRequest request,
        CancellationToken cancellationToken)
    {
        if (!_mlOptions.ManagementApiEnabled) return NotFound();
        if (string.IsNullOrWhiteSpace(forecastId)) return BadRequest("forecastId is required.");
        if (!Enum.IsDefined(request.Kind)) return BadRequest("kind is invalid.");
        var actor = ResolveActor(request.RecordedBy);
        if (actor is null) return BadRequest("recordedBy is required.");
        if (request.ObservedAtUtc == default) return BadRequest("observedAtUtc is required.");
        if (string.IsNullOrWhiteSpace(request.Note)) return BadRequest("note is required.");
        var outcome = new AssetFailureForecastOutcome
        {
            OutcomeId = AssetFailureForecastIdentity.CreateOutcomeId(
                forecastId.Trim(),
                request.Kind,
                request.ObservedAtUtc,
                actor),
            ForecastId = forecastId.Trim(),
            Kind = request.Kind,
            ObservedAtUtc = request.ObservedAtUtc,
            RecordedBy = actor,
            Note = request.Note.Trim()
        };
        try
        {
            await _service.AppendOutcomeAsync(outcome, cancellationToken);
            return Ok(outcome);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private string? ResolveActor(string? requestedActor)
    {
        var identity = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        if (!string.IsNullOrWhiteSpace(identity)) return identity.Trim();
        return string.IsNullOrWhiteSpace(requestedActor) ? null : requestedActor.Trim();
    }
}

public sealed class AssetFailureForecastOutcomeRequest
{
    public AssetFailureForecastOutcomeKind Kind { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string? RecordedBy { get; set; }
    public string? Note { get; set; }
}
