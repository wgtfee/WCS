namespace Wcs.Host.Controllers;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.RootCause;

/// <summary>
/// 仅 LoadTest 环境启用，用于验证 v3.6 图版本、分析幂等、传播路径、人工复核和重启恢复。
/// </summary>
[ApiController]
[Route("api/anomaly/root-cause/load")]
public sealed class AnomalyRootCauseLoadController : ControllerBase
{
    private readonly IAssetHealthRootCauseAnalysisEngine _engine;
    private readonly IAssetHealthRootCauseAnalysisStore _store;
    private readonly IHostEnvironment _environment;

    public AnomalyRootCauseLoadController(
        IAssetHealthRootCauseAnalysisEngine engine,
        IAssetHealthRootCauseAnalysisStore store,
        IHostEnvironment environment)
    {
        _engine = engine;
        _store = store;
        _environment = environment;
    }

    [HttpPost("analyses")]
    public async Task<ActionResult> Analyze(
        [FromBody] AssetHealthRootCauseLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        if (request.Events.Count == 0 || request.Events.Count > 10_000)
            return BadRequest("events must contain between 1 and 10000 items.");

        var events = request.Events.Select(ToEvent).ToArray();
        var trigger = string.IsNullOrWhiteSpace(request.TriggerEventId)
            ? events[0]
            : events.FirstOrDefault(item => item.EventId == request.TriggerEventId.Trim());
        if (trigger is null) return BadRequest("triggerEventId was not found in events.");

        await _store.InitializeAsync(cancellationToken);
        await _store.RegisterGraphAsync(_engine.GraphRegistration, cancellationToken);
        var analysis = _engine.Analyze(trigger, events, DateTime.UtcNow);
        if (analysis is null)
            return UnprocessableEntity("Trigger event is inactive, outside the graph, or root cause analysis is disabled.");
        var inserted = await _store.SaveAsync(analysis, cancellationToken);
        return Ok(new
        {
            inserted,
            analysis,
            status = await _store.GetStatusAsync(cancellationToken)
        });
    }

    [HttpPost("maintain")]
    public async Task<ActionResult> Maintain(CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();
        await _store.MaintainAsync(DateTime.UtcNow, cancellationToken);
        return Ok(await _store.GetStatusAsync(cancellationToken));
    }

    private static AssetHealthEventSnapshot ToEvent(AssetHealthRootCauseLoadEvent item)
    {
        var eventId = string.IsNullOrWhiteSpace(item.EventId)
            ? Guid.NewGuid().ToString("N")
            : item.EventId.Trim();
        var assetId = item.AssetId?.Trim() ?? string.Empty;
        if (assetId.Length == 0)
            throw new ArgumentException("Every load event requires assetId.");
        var first = NormalizeUtc(item.FirstDetectedUtc == default ? DateTime.UtcNow : item.FirstDetectedUtc);
        var last = NormalizeUtc(item.LastObservedUtc == default ? first : item.LastObservedUtc);
        if (last < first) throw new ArgumentException("lastObservedUtc cannot be earlier than firstDetectedUtc.");
        var grade = item.Grade ?? ResolveGrade(item.HealthScore);
        var score = Math.Clamp(item.HealthScore, 0, 100);
        return new AssetHealthEventSnapshot
        {
            EventId = eventId,
            EventKey = assetId,
            AssetId = assetId,
            Version = Math.Max(1, item.Version),
            LifecycleStatus = AssetHealthEventLifecycleStatus.Active,
            Grade = grade,
            PeakGrade = grade,
            HealthScore = score,
            LowestHealthScore = score,
            FirstDetectedUtc = first,
            LastObservedUtc = last,
            Acknowledged = false,
            IsSuppressed = false,
            Reason = string.IsNullOrWhiteSpace(item.Reason) ? $"LoadTest {assetId} anomaly." : item.Reason.Trim(),
            Source = string.IsNullOrWhiteSpace(item.Source) ? "LoadTest" : item.Source.Trim(),
            Category = string.IsNullOrWhiteSpace(item.Category) ? "Deterministic" : item.Category.Trim()
        };
    }

    private static AssetHealthGrade ResolveGrade(double score) => score switch
    {
        >= 85 => AssetHealthGrade.Healthy,
        >= 70 => AssetHealthGrade.Attention,
        >= 40 => AssetHealthGrade.Degraded,
        _ => AssetHealthGrade.Critical
    };

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class AssetHealthRootCauseLoadRequest
{
    public string? TriggerEventId { get; set; }
    public List<AssetHealthRootCauseLoadEvent> Events { get; set; } = new();
}

public sealed class AssetHealthRootCauseLoadEvent
{
    public string EventId { get; set; } = string.Empty;
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public double HealthScore { get; set; } = 50;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AssetHealthGrade? Grade { get; set; }

    public DateTime FirstDetectedUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
    public string? Reason { get; set; }
    public string? Source { get; set; }
    public string? Category { get; set; }
}
