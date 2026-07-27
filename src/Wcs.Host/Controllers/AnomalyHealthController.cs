namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.HealthScoring;

[ApiController]
[Route("api/anomaly/health")]
public sealed class AnomalyHealthController : ControllerBase
{
    private readonly IAssetHealthScoringService _healthScoring;
    private readonly IAssetHealthScoreHistoryStore _history;

    public AnomalyHealthController(
        IAssetHealthScoringService healthScoring,
        IAssetHealthScoreHistoryStore history)
    {
        _healthScoring = healthScoring;
        _history = history;
    }

    [HttpGet("status")]
    public ActionResult<AssetHealthScoringStatus> GetStatus() =>
        Ok(_healthScoring.GetStatus());

    [HttpGet("history/status")]
    public async Task<ActionResult<AssetHealthHistoryStoreStatus>> GetHistoryStatus(
        CancellationToken cancellationToken) =>
        Ok(await _history.GetStatusAsync(cancellationToken));

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

    [HttpGet("assets/{assetId}/history")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthScorePoint>>> GetHistory(
        string assetId,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        var result = await _history.GetHistoryAsync(
            assetId.Trim(),
            fromUtc,
            Math.Clamp(maxCount, 1, 10_000),
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("assets/{assetId}/history/page")]
    public async Task<ActionResult<AssetHealthHistoryPage>> GetHistoryPage(
        string assetId,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int skip = 0,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
            return BadRequest("fromUtc cannot be later than toUtc.");

        var result = await _history.GetHistoryPageAsync(
            assetId.Trim(),
            fromUtc,
            toUtc,
            Math.Max(0, skip),
            Math.Clamp(maxCount, 1, 10_000),
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("assets/{assetId}/trend")]
    public async Task<ActionResult<AssetHealthTrendSnapshot>> GetTrend(
        string assetId,
        [FromQuery] int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        var trend = await _history.GetTrendAsync(
            assetId.Trim(),
            windowSize,
            cancellationToken);
        return trend is null ? NotFound() : Ok(trend);
    }

    [HttpGet("assets/{assetId}/trend/range")]
    public async Task<ActionResult<AssetHealthTrendSnapshot>> GetTrendRange(
        string assetId,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return BadRequest();
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
            return BadRequest("fromUtc cannot be later than toUtc.");

        var trend = await _history.GetTrendRangeAsync(
            assetId.Trim(),
            fromUtc,
            toUtc,
            windowSize,
            cancellationToken);
        return trend is null ? NotFound() : Ok(trend);
    }
}