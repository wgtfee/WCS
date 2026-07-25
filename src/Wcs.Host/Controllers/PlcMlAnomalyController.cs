namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/ml")]
public sealed class PlcMlAnomalyController : ControllerBase
{
    private readonly IPlcMlAnomalyEngine _engine;

    public PlcMlAnomalyController(IPlcMlAnomalyEngine engine)
    {
        _engine = engine;
    }

    [HttpGet("status")]
    public ActionResult<IReadOnlyList<PlcMlProfileStatus>> GetStatus() => Ok(_engine.GetStatus());

    [HttpPost("train/{profileId}")]
    public async Task<ActionResult<PlcMlTrainingResult>> Train(
        string profileId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(() => _engine.TrainAsync(profileId, cancellationToken));

    [HttpGet("models/{profileId}")]
    public async Task<ActionResult<IReadOnlyList<PlcMlModelVersionInfo>>> ListModels(
        string profileId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(() => _engine.ListModelsAsync(profileId, cancellationToken));

    [HttpPost("models/{profileId}/{version}/activate")]
    public async Task<ActionResult<PlcMlModelVersionInfo>> ActivateModel(
        string profileId,
        string version,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(() => _engine.ActivateModelAsync(profileId, version, cancellationToken));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
