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
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _engine.TrainAsync(profileId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
