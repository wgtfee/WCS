namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Fusion;

[ApiController]
[Route("api/anomaly/fusion")]
public sealed class AnomalyFusionController : ControllerBase
{
    private readonly IAnomalyFusionEngine _engine;

    public AnomalyFusionController(IAnomalyFusionEngine engine)
    {
        _engine = engine;
    }

    [HttpGet("status")]
    public ActionResult<AnomalyFusionStatus> GetStatus() => Ok(_engine.GetStatus());

    [HttpGet("assets")]
    public ActionResult<IReadOnlyList<FusedHealthSnapshot>> GetAssets(
        [FromQuery] FusedHealthStatus? minimumStatus = null,
        [FromQuery] int maxCount = 200) =>
        Ok(_engine.GetAssets(minimumStatus, Math.Clamp(maxCount, 1, 5000)));

    [HttpGet("assets/{assetId}")]
    public ActionResult<FusedHealthSnapshot> GetAsset(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        var snapshot = _engine.GetAsset(assetId.Trim());
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}
