namespace Wcs.Core.EventBus.Persistence;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 事件重放服务 — 系统恢复后重放快照时间点之后的事件
/// </summary>
public class EventReplayService
{
    private readonly IEventStore _eventStore;
    private readonly IEventBus _eventBus;
    private readonly ILogger<EventReplayService>? _logger;

    /// <summary>
    /// 可重放的事件类型白名单 — 只有影响系统状态的事件才应重放
    /// 衍生事件（如报警）由状态变化重新触发，不应重放
    /// </summary>
    private static readonly HashSet<Type> ReplayableEventTypes = new()
    {
        typeof(DeviceStateChangedEvent),
        typeof(TaskStateChangedEvent),
        typeof(ObjectLocationChangedEvent),
        typeof(PlcBlockChangedEvent)
    };

    public EventReplayService(
        IEventStore eventStore,
        IEventBus eventBus,
        ILogger<EventReplayService>? logger = null)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _logger = logger;
    }

    /// <summary>
    /// 重放自快照时间点之后的可重放事件
    /// </summary>
    /// <param name="sinceTime">快照时间点（只重放此之后的事件）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>重放的事件数量</returns>
    public async Task<int> ReplayAsync(DateTime sinceTime, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        _logger?.LogInformation("EventReplay: replaying events from {SinceTime:O} to {Now:O}", sinceTime, now);

        var events = await _eventStore.QueryAsync(sinceTime, now, ct);
        var replayable = events
            .Where(e => ReplayableEventTypes.Contains(e.GetType()))
            .OrderBy(e => e.OccurTime)
            .ToList();

        if (replayable.Count == 0)
        {
            _logger?.LogInformation("EventReplay: no replayable events found");
            return 0;
        }

        _logger?.LogInformation("EventReplay: replaying {Count} of {Total} events",
            replayable.Count, events.Count);

        var replayed = 0;
        foreach (var evt in replayable)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await _eventBus.PublishAsync(evt, ct);
                replayed++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "EventReplay: failed to replay event {EventId} ({EventType})",
                    evt.EventId, evt.GetType().Name);
            }
        }

        _logger?.LogInformation("EventReplay: completed — replayed {Replayed} events", replayed);
        return replayed;
    }

    /// <summary>
    /// 注册自定义可重放事件类型
    /// </summary>
    public void RegisterReplayableType<T>() where T : IEvent
    {
        ReplayableEventTypes.Add(typeof(T));
    }
}
