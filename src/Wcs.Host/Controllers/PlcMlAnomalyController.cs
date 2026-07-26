namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/ml")]
public sealed class PlcMlAnomalyController : ControllerBase
{
    private readonly IPlcMlAnomalyEngine _engine;
    private readonly PlcMlAnomalyOptions _options;

    public PlcMlAnomalyController(IPlcMlAnomalyEngine engine, PlcMlAnomalyOptions options)
    {
        _engine = engine;
        _options = options;
    }

    [HttpGet("status")]
    public ActionResult<IReadOnlyList<PlcMlProfileStatus>> GetStatus() => Ok(_engine.GetStatus());

    [HttpPost("train/{profileId}")]
    public async Task<ActionResult<PlcMlTrainingResult>> Train(
        string profileId,
        [FromBody] PlcMlTrainRequest? request,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _engine.TrainAsync(
            profileId,
            request?.DatasetVersion,
            request?.RequestedBy,
            cancellationToken));
    }

    [HttpGet("models/{profileId}")]
    public async Task<ActionResult<IReadOnlyList<PlcMlModelVersionInfo>>> ListModels(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _engine.ListModelsAsync(profileId, cancellationToken));
    }

    [HttpPost("models/{profileId}/{version}/activate")]
    public async Task<ActionResult<PlcMlModelVersionInfo>> ActivateModel(
        string profileId,
        string version,
        CancellationToken cancellationToken)
    {
        if (!_options.ManagementApiEnabled) return NotFound();
        return await ExecuteAsync(() => _engine.ActivateModelAsync(profileId, version, cancellationToken));
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

public sealed class PlcMlTrainRequest
{
    public string? DatasetVersion { get; set; }
    public string? RequestedBy { get; set; }
}
