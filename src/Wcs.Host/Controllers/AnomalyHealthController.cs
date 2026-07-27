namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.HealthScoring;

[ApiController]
[Route("api/anomaly/health")]
public sealed class AnomalyHealthController : ControllerBase
{
    private readonly IAssetHealthScoringService _healthScoring;

    public AnomalyHealthController(IAssetHealthScoringService healthScoring) =>
        _healthScoring = healthScoring;

    [HttpGet("status")]
    public ActionResult<AssetHealthScoringStatus> GetStatus() =>
        Ok(_healthScoring.GetStatus());

    [HttpGet("assets")]
    public ActionResult<IReadOnlyList<AssetHealthScoreSnapshot>> GetAssets(
        [FromQuery] AssetHealthGrade? minimumGrade = null,
        [FromQuery] int maxCount = 200) =>
        Ok(_healthScoring.GetAssets(minimumGrade, Math.Clamp(maxCount, 1, 5000)));

    [HttpGet("assets/{assetId}")]
    public ActionResult<AssetHealthScoreSnapshot> GetAsset(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        var snapshot = _healthScoring.GetAsset(assetId.Trim());
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}
