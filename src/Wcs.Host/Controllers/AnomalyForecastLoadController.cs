namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

[ApiController]
[Route("api/anomaly/forecast/load")]
public sealed class AnomalyForecastLoadController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IAssetHealthScoreHistoryStore _historyStore;

    public AnomalyForecastLoadController(
        IHostEnvironment environment,
        IAssetHealthScoreHistoryStore historyStore)
    {
        _environment = environment;
        _historyStore = historyStore;
    }

    [HttpPost("history")]
    public async Task<ActionResult> SeedHistory(
        [FromBody] AssetFailureForecastHistoryLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("ForecastLoadTest")) return NotFound();
        var assetId = request.AssetId?.Trim() ?? string.Empty;
        if (assetId.Length == 0) return BadRequest("assetId is required.");
        var count = Math.Clamp(request.Count, 1, 5_000);
        var intervalMinutes = Math.Clamp(request.IntervalMinutes, 1, 1_440);
        var startUtc = request.StartUtc == default
            ? new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            : request.StartUtc.ToUniversalTime();
        var accepted = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = Math.Clamp(request.StartHealthScore + request.HealthScoreDelta * index, 0, 100);
            var risk = Math.Clamp(request.StartFusionRisk + request.FusionRiskDelta * index, 0, 1);
            var timestamp = startUtc.AddMinutes(intervalMinutes * (long)index);
            var grade = ResolveGrade(score);
            var snapshot = new AssetHealthScoreSnapshot
            {
                AssetId = assetId,
                HealthScore = score,
                Grade = grade,
                FusionRiskScore = risk,
                FusionStatus = ResolveFusionStatus(risk),
                IndependentSourceCount = 2,
                CalculatedAtUtc = timestamp,
                Factors = Array.Empty<AssetHealthFactor>(),
                Summary = "ForecastLoadTest deterministic health history."
            };
            if (await _historyStore.RecordAsync(snapshot, timestamp, cancellationToken)) accepted++;
        }

        return Ok(new
        {
            assetId,
            requested = count,
            accepted,
            startUtc,
            endUtc = startUtc.AddMinutes(intervalMinutes * (long)(count - 1)),
            intervalMinutes,
            controlWrites = 0
        });
    }

    private static AssetHealthGrade ResolveGrade(double score) => score switch
    {
        >= 85 => AssetHealthGrade.Healthy,
        >= 70 => AssetHealthGrade.Attention,
        >= 40 => AssetHealthGrade.Degraded,
        _ => AssetHealthGrade.Critical
    };

    private static FusedHealthStatus ResolveFusionStatus(double risk) => risk switch
    {
        >= 0.85 => FusedHealthStatus.Alarm,
        >= 0.65 => FusedHealthStatus.Warning,
        >= 0.35 => FusedHealthStatus.Observe,
        _ => FusedHealthStatus.Normal
    };
}

public sealed class AssetFailureForecastHistoryLoadRequest
{
    public string? AssetId { get; set; }
    public int Count { get; set; } = 48;
    public int IntervalMinutes { get; set; } = 60;
    public DateTime StartUtc { get; set; }
    public double StartHealthScore { get; set; } = 90;
    public double HealthScoreDelta { get; set; } = -0.5;
    public double StartFusionRisk { get; set; } = 0.10;
    public double FusionRiskDelta { get; set; } = 0.01;
}
