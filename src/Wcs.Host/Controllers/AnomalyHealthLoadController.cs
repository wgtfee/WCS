namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 仅 LoadTest 环境启用，用于验证健康历史 SQL 幂等、重启恢复、数据库中断和保留期清理。
/// </summary>
[ApiController]
[Route("api/anomaly/health/load")]
public sealed class AnomalyHealthLoadController : ControllerBase
{
    private readonly IAssetHealthScoreHistoryStore _history;
    private readonly IHostEnvironment _environment;

    public AnomalyHealthLoadController(
        IAssetHealthScoreHistoryStore history,
        IHostEnvironment environment)
    {
        _history = history;
        _environment = environment;
    }

    [HttpPost("points")]
    public async Task<ActionResult> RecordPoints(
        [FromBody] AnomalyHealthLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        if (string.IsNullOrWhiteSpace(request.AssetId) || request.Points.Count == 0)
            return BadRequest();

        var accepted = 0;
        foreach (var item in request.Points.Take(10_000))
        {
            var score = Math.Clamp(item.HealthScore, 0, 100);
            var grade = item.Grade ?? ResolveGrade(score);
            var recordedAt = item.RecordedAtUtc == default ? DateTime.UtcNow : item.RecordedAtUtc;
            var snapshot = new AssetHealthScoreSnapshot
            {
                AssetId = request.AssetId.Trim(),
                HealthScore = score,
                Grade = grade,
                FusionRiskScore = Math.Round(1 - (score / 100), 4, MidpointRounding.AwayFromZero),
                FusionStatus = grade switch
                {
                    AssetHealthGrade.Healthy => FusedHealthStatus.Normal,
                    AssetHealthGrade.Attention => FusedHealthStatus.Observe,
                    AssetHealthGrade.Degraded => FusedHealthStatus.Warning,
                    _ => FusedHealthStatus.Alarm
                },
                IndependentSourceCount = item.IndependentSourceCount,
                CalculatedAtUtc = item.CalculatedAtUtc == default ? recordedAt : item.CalculatedAtUtc,
                Factors = Array.Empty<AssetHealthFactor>(),
                Summary = string.IsNullOrWhiteSpace(item.Summary)
                    ? $"LoadTest health score {score:F2}."
                    : item.Summary.Trim()
            };

            if (await _history.RecordAsync(snapshot, recordedAt, cancellationToken))
                accepted++;
        }

        return Ok(new
        {
            request.AssetId,
            requested = request.Points.Count,
            accepted,
            status = await _history.GetStatusAsync(cancellationToken)
        });
    }

    [HttpPost("maintain")]
    public async Task<ActionResult> Maintain(CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        await _history.MaintainAsync(DateTime.UtcNow, cancellationToken);
        return Ok(await _history.GetStatusAsync(cancellationToken));
    }

    private static AssetHealthGrade ResolveGrade(double score) => score switch
    {
        >= 85 => AssetHealthGrade.Healthy,
        >= 70 => AssetHealthGrade.Attention,
        >= 40 => AssetHealthGrade.Degraded,
        _ => AssetHealthGrade.Critical
    };
}

public sealed class AnomalyHealthLoadRequest
{
    public string AssetId { get; set; } = "HEALTH-SQL-E2E";
    public List<AnomalyHealthLoadPoint> Points { get; set; } = new();
}

public sealed class AnomalyHealthLoadPoint
{
    public double HealthScore { get; set; }
    public AssetHealthGrade? Grade { get; set; }
    public int IndependentSourceCount { get; set; } = 1;
    public DateTime CalculatedAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string? Summary { get; set; }
}