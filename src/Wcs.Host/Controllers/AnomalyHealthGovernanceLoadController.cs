namespace Wcs.Host.Controllers;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 仅 LoadTest 环境启用，用于验证健康事件生命周期、SQL Journal 和 MES Outbox。
/// </summary>
[ApiController]
[Route("api/anomaly/health-governance/load")]
public sealed class AnomalyHealthGovernanceLoadController : ControllerBase
{
    private readonly IAssetHealthGovernanceService _governance;
    private readonly IHostEnvironment _environment;

    public AnomalyHealthGovernanceLoadController(
        IAssetHealthGovernanceService governance,
        IHostEnvironment environment)
    {
        _governance = governance;
        _environment = environment;
    }

    [HttpPost("evaluations")]
    public async Task<ActionResult> Evaluate(
        [FromBody] AssetHealthGovernanceLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        if (string.IsNullOrWhiteSpace(request.AssetId) || request.Points.Count == 0)
            return BadRequest();

        var transitions = new List<AssetHealthEventTransition>();
        foreach (var point in request.Points.Take(10_000))
        {
            var score = Math.Clamp(point.HealthScore, 0, 100);
            var grade = point.Grade ?? ResolveGrade(score);
            var evaluatedAt = point.EvaluatedAtUtc == default ? DateTime.UtcNow : point.EvaluatedAtUtc;
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
                IndependentSourceCount = point.IndependentSourceCount,
                CalculatedAtUtc = evaluatedAt,
                Factors = new[]
                {
                    new AssetHealthFactor
                    {
                        Source = string.IsNullOrWhiteSpace(point.Source) ? "LoadTest" : point.Source.Trim(),
                        Category = string.IsNullOrWhiteSpace(point.Category) ? "Deterministic" : point.Category.Trim(),
                        Contribution = 1,
                        Penalty = 100 - score,
                        Reason = string.IsNullOrWhiteSpace(point.Reason)
                            ? $"LoadTest health score {score:F2}."
                            : point.Reason.Trim()
                    }
                },
                Summary = string.IsNullOrWhiteSpace(point.Reason)
                    ? $"LoadTest health score {score:F2}."
                    : point.Reason.Trim()
            };

            var result = await _governance.EvaluateAsync(snapshot, evaluatedAt, cancellationToken);
            transitions.AddRange(result);
        }

        return Ok(new
        {
            request.AssetId,
            requested = request.Points.Count,
            transitions,
            status = _governance.GetStatus()
        });
    }

    [HttpPost("maintain")]
    public async Task<ActionResult> Maintain(CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        await _governance.MaintainAsync(DateTime.UtcNow, cancellationToken);
        return Ok(_governance.GetStatus());
    }

    private static AssetHealthGrade ResolveGrade(double score) => score switch
    {
        >= 85 => AssetHealthGrade.Healthy,
        >= 70 => AssetHealthGrade.Attention,
        >= 40 => AssetHealthGrade.Degraded,
        _ => AssetHealthGrade.Critical
    };
}

public sealed class AssetHealthGovernanceLoadRequest
{
    public string AssetId { get; set; } = "HEALTH-GOV-E2E";
    public List<AssetHealthGovernanceLoadPoint> Points { get; set; } = new();
}

public sealed class AssetHealthGovernanceLoadPoint
{
    public double HealthScore { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AssetHealthGrade? Grade { get; set; }

    public int IndependentSourceCount { get; set; } = 2;
    public DateTime EvaluatedAtUtc { get; set; }
    public string? Source { get; set; }
    public string? Category { get; set; }
    public string? Reason { get; set; }
}
