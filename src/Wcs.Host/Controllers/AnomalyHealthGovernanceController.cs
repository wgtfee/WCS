namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

[ApiController]
[Route("api/anomaly/health-governance")]
public sealed class AnomalyHealthGovernanceController : ControllerBase
{
    private readonly IAssetHealthGovernanceService _governance;
    private readonly IAssetHealthEventJournalStore _journal;

    public AnomalyHealthGovernanceController(
        IAssetHealthGovernanceService governance,
        IAssetHealthEventJournalStore journal)
    {
        _governance = governance;
        _journal = journal;
    }

    [HttpGet("status")]
    public async Task<ActionResult<AssetHealthGovernanceRuntimeStatus>> GetStatus(
        CancellationToken cancellationToken) =>
        Ok(new AssetHealthGovernanceRuntimeStatus
        {
            Governance = _governance.GetStatus(),
            Journal = await _journal.GetStatusAsync(cancellationToken)
        });

    [HttpGet("events")]
    public ActionResult<IReadOnlyList<AssetHealthEventSnapshot>> GetEvents(
        [FromQuery] AssetHealthEventLifecycleStatus? lifecycleStatus = null,
        [FromQuery] AssetHealthGrade? minimumGrade = null,
        [FromQuery] int maxCount = 200) =>
        Ok(_governance.GetEvents(
            lifecycleStatus,
            minimumGrade,
            Math.Clamp(maxCount, 1, 10_000)));

    [HttpGet("events/{eventId}")]
    public ActionResult<AssetHealthEventSnapshot> GetEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return BadRequest();
        var result = _governance.GetEvent(eventId.Trim());
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("events/{eventId}/history")]
    public async Task<ActionResult<IReadOnlyList<AssetHealthEventTransition>>> GetHistory(
        string eventId,
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return BadRequest();
        return Ok(await _journal.GetHistoryAsync(
            eventId.Trim(),
            Math.Clamp(maxCount, 1, 10_000),
            cancellationToken));
    }

    [HttpPost("events/{eventId}/acknowledge")]
    public async Task<ActionResult<AssetHealthEventSnapshot>> Acknowledge(
        string eventId,
        [FromBody] AssetHealthAcknowledgeRequest request,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(request.Actor);
        if (string.IsNullOrWhiteSpace(eventId) || actor is null)
            return BadRequest("eventId and actor are required.");

        var result = await _governance.AcknowledgeAsync(
            eventId.Trim(),
            actor,
            request.Note,
            DateTime.UtcNow,
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("events/{eventId}/suppress")]
    public async Task<ActionResult<AssetHealthEventSnapshot>> Suppress(
        string eventId,
        [FromBody] AssetHealthSuppressRequest request,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(request.Actor);
        if (string.IsNullOrWhiteSpace(eventId) ||
            actor is null ||
            string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("eventId, actor and reason are required.");
        if (request.UntilUtc is not null && request.UntilUtc <= DateTime.UtcNow)
            return BadRequest("untilUtc must be in the future.");

        try
        {
            var result = await _governance.SuppressAsync(
                eventId.Trim(),
                actor,
                request.Reason.Trim(),
                request.UntilUtc,
                DateTime.UtcNow,
                cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    [HttpPost("events/{eventId}/unsuppress")]
    public async Task<ActionResult<AssetHealthEventSnapshot>> Unsuppress(
        string eventId,
        [FromBody] AssetHealthUnsuppressRequest request,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(request.Actor);
        if (string.IsNullOrWhiteSpace(eventId) || actor is null)
            return BadRequest("eventId and actor are required.");

        var result = await _governance.UnsuppressAsync(
            eventId.Trim(),
            actor,
            request.Note,
            DateTime.UtcNow,
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("deliveries/{messageId}/retry")]
    public async Task<ActionResult> RetryDelivery(
        string messageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return BadRequest();
        var accepted = await _journal.RetryDeliveryAsync(
            messageId.Trim(),
            DateTime.UtcNow,
            cancellationToken);
        return accepted ? Accepted() : Conflict("Message does not exist or was already delivered.");
    }

    private string? ResolveActor(string? requestedActor)
    {
        var identity = User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        if (!string.IsNullOrWhiteSpace(identity)) return identity.Trim();
        return string.IsNullOrWhiteSpace(requestedActor) ? null : requestedActor.Trim();
    }
}

public sealed record AssetHealthGovernanceRuntimeStatus
{
    public required AssetHealthGovernanceStatus Governance { get; init; }
    public required AssetHealthEventJournalStatus Journal { get; init; }
}

public sealed class AssetHealthAcknowledgeRequest
{
    public string? Actor { get; set; }
    public string? Note { get; set; }
}

public sealed class AssetHealthSuppressRequest
{
    public string? Actor { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime? UntilUtc { get; set; }
}

public sealed class AssetHealthUnsuppressRequest
{
    public string? Actor { get; set; }
    public string? Note { get; set; }
}
