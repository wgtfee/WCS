namespace Wcs.Core.AnomalyDetection.HealthGovernance;

using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 将只读资产健康评分转换为有确认、抑制、恢复和审计记录的健康事件。
/// 本服务不写 PLC、不停止设备，也不修改任务、路径、路权或调度结果。
/// </summary>
public sealed class AssetHealthGovernanceService : IAssetHealthGovernanceService
{
    private readonly AssetHealthGovernanceOptions _options;
    private readonly AssetHealthScoringOptions _healthOptions;
    private readonly IAssetHealthEventJournalStore _journal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, TrackedAssetState> _assetStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetHealthEventSnapshot> _eventsById = new(StringComparer.Ordinal);

    private DateTime? _lastEvaluationUtc;
    private string? _lastError;

    public AssetHealthGovernanceService(
        AssetHealthGovernanceOptions options,
        AssetHealthScoringOptions healthOptions,
        IAssetHealthEventJournalStore journal)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _healthOptions = healthOptions ?? throw new ArgumentNullException(nameof(healthOptions));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async ValueTask<IReadOnlyList<AssetHealthEventTransition>> EvaluateAsync(
        AssetHealthScoreSnapshot snapshot,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(snapshot.AssetId))
            return Array.Empty<AssetHealthEventTransition>();

        utcNow = NormalizeUtc(utcNow);
        var assetId = snapshot.AssetId.Trim();
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            TrackedAssetState state;
            lock (_stateGate)
            {
                if (!_assetStates.TryGetValue(assetId, out var existing))
                {
                    if (!TryMakeTrackingCapacity())
                        return Array.Empty<AssetHealthEventTransition>();
                    existing = new TrackedAssetState(assetId);
                }
                state = existing.Clone();
            }

            state.LastEvaluatedUtc = utcNow;
            var transitions = new List<AssetHealthEventTransition>(2);
            var current = state.ActiveEvent;

            if (current is not null && IsSuppressionExpired(current, utcNow))
            {
                current = current with
                {
                    Version = current.Version + 1,
                    IsSuppressed = false,
                    SuppressedUntilUtc = null,
                    SuppressedReason = null,
                    LastObservedUtc = utcNow
                };
                transitions.Add(CreateTransition(
                    current,
                    AssetHealthEventTransitionType.Unsuppressed,
                    utcNow,
                    "system",
                    "Suppression window expired."));
                state.ActiveEvent = current;
                state.LastJournaledUtc = utcNow;
            }

            if (IsAtLeast(snapshot.Grade, _options.MinimumEventGrade))
            {
                state.ConsecutiveUnhealthy++;
                state.ConsecutiveRecovery = 0;

                if (current is null)
                {
                    if (state.ConsecutiveUnhealthy >= _options.ConsecutiveUnhealthyEvaluations)
                    {
                        current = CreateRaisedEvent(snapshot, utcNow);
                        transitions.Add(CreateTransition(
                            current,
                            AssetHealthEventTransitionType.Raised,
                            utcNow,
                            null,
                            null));
                        state.ActiveEvent = current;
                        state.LastJournaledUtc = utcNow;
                    }
                }
                else
                {
                    var factor = ResolvePrimaryFactor(snapshot);
                    var updated = current with
                    {
                        Grade = snapshot.Grade,
                        PeakGrade = IsMoreSevere(snapshot.Grade, current.PeakGrade)
                            ? snapshot.Grade
                            : current.PeakGrade,
                        HealthScore = snapshot.HealthScore,
                        LowestHealthScore = Math.Min(current.LowestHealthScore, snapshot.HealthScore),
                        LastObservedUtc = utcNow,
                        Reason = factor.Reason,
                        Source = factor.Source,
                        Category = factor.Category
                    };

                    if (updated.Grade != current.Grade)
                    {
                        updated = updated with { Version = current.Version + 1 };
                        transitions.Add(CreateTransition(
                            updated,
                            AssetHealthEventTransitionType.GradeChanged,
                            utcNow,
                            null,
                            $"Health grade changed from {current.Grade} to {updated.Grade}."));
                        state.LastJournaledUtc = utcNow;
                    }
                    else if (utcNow - state.LastJournaledUtc >=
                             TimeSpan.FromSeconds(_options.MaximumUnchangedEventIntervalSeconds))
                    {
                        updated = updated with { Version = current.Version + 1 };
                        transitions.Add(CreateTransition(
                            updated,
                            AssetHealthEventTransitionType.Observed,
                            utcNow,
                            null,
                            "Active health event heartbeat."));
                        state.LastJournaledUtc = utcNow;
                    }

                    current = updated;
                    state.ActiveEvent = updated;
                }
            }
            else
            {
                state.ConsecutiveUnhealthy = 0;
                if (current is null)
                {
                    state.ConsecutiveRecovery = 0;
                }
                else
                {
                    state.ConsecutiveRecovery++;
                    if (state.ConsecutiveRecovery >= _options.ConsecutiveRecoveryEvaluations)
                    {
                        var factor = ResolvePrimaryFactor(snapshot);
                        var recovered = current with
                        {
                            Version = current.Version + 1,
                            LifecycleStatus = AssetHealthEventLifecycleStatus.Recovered,
                            Grade = snapshot.Grade,
                            HealthScore = snapshot.HealthScore,
                            LastObservedUtc = utcNow,
                            RecoveredAtUtc = utcNow,
                            IsSuppressed = false,
                            SuppressedUntilUtc = null,
                            SuppressedReason = null,
                            Reason = factor.Reason,
                            Source = factor.Source,
                            Category = factor.Category
                        };
                        transitions.Add(CreateTransition(
                            recovered,
                            AssetHealthEventTransitionType.Recovered,
                            utcNow,
                            null,
                            "Health score recovered below the event threshold."));
                        current = recovered;
                        state.ActiveEvent = null;
                        state.ConsecutiveRecovery = 0;
                        state.LastJournaledUtc = utcNow;
                    }
                }
            }

            foreach (var transition in transitions)
                await _journal.AppendAsync(transition, cancellationToken);

            lock (_stateGate)
            {
                _assetStates[assetId] = state;
                if (current is not null)
                    _eventsById[current.EventId] = current;
                foreach (var transition in transitions)
                    _eventsById[transition.Event.EventId] = transition.Event;
                _lastEvaluationUtc = utcNow;
                _lastError = null;
            }

            return transitions;
        }
        catch (Exception exception)
        {
            lock (_stateGate) _lastError = exception.Message;
            throw;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ValueTask<AssetHealthEventSnapshot?> AcknowledgeAsync(
        string eventId,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(actor))
            return ValueTask.FromResult<AssetHealthEventSnapshot?>(null);

        var normalizedActor = actor.Trim();
        return MutateEventAsync(
            eventId.Trim(),
            utcNow,
            cancellationToken,
            current => current.Acknowledged
                ? null
                : new EventMutation(
                    current with
                    {
                        Version = current.Version + 1,
                        Acknowledged = true,
                        AcknowledgedAtUtc = NormalizeUtc(utcNow),
                        AcknowledgedBy = normalizedActor
                    },
                    AssetHealthEventTransitionType.Acknowledged,
                    normalizedActor,
                    note));
    }

    public ValueTask<AssetHealthEventSnapshot?> SuppressAsync(
        string eventId,
        string actor,
        string reason,
        DateTime? untilUtc,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) ||
            string.IsNullOrWhiteSpace(actor) ||
            string.IsNullOrWhiteSpace(reason))
            return ValueTask.FromResult<AssetHealthEventSnapshot?>(null);

        utcNow = NormalizeUtc(utcNow);
        var normalizedUntil = untilUtc is null ? null : NormalizeUtc(untilUtc.Value);
        if (normalizedUntil is not null && normalizedUntil <= utcNow)
            throw new ArgumentOutOfRangeException(nameof(untilUtc), "Suppression end must be in the future.");

        var normalizedActor = actor.Trim();
        var normalizedReason = reason.Trim();
        return MutateEventAsync(
            eventId.Trim(),
            utcNow,
            cancellationToken,
            current => current.IsSuppressed && current.SuppressedUntilUtc == normalizedUntil
                ? null
                : new EventMutation(
                    current with
                    {
                        Version = current.Version + 1,
                        IsSuppressed = true,
                        SuppressedUntilUtc = normalizedUntil,
                        SuppressedReason = normalizedReason
                    },
                    AssetHealthEventTransitionType.Suppressed,
                    normalizedActor,
                    normalizedReason));
    }

    public ValueTask<AssetHealthEventSnapshot?> UnsuppressAsync(
        string eventId,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(actor))
            return ValueTask.FromResult<AssetHealthEventSnapshot?>(null);

        var normalizedActor = actor.Trim();
        return MutateEventAsync(
            eventId.Trim(),
            utcNow,
            cancellationToken,
            current => !current.IsSuppressed
                ? null
                : new EventMutation(
                    current with
                    {
                        Version = current.Version + 1,
                        IsSuppressed = false,
                        SuppressedUntilUtc = null,
                        SuppressedReason = null
                    },
                    AssetHealthEventTransitionType.Unsuppressed,
                    normalizedActor,
                    note));
    }

    public void Restore(IReadOnlyList<AssetHealthEventTransition> latestTransitions)
    {
        ArgumentNullException.ThrowIfNull(latestTransitions);
        lock (_stateGate)
        {
            _assetStates.Clear();
            _eventsById.Clear();
            foreach (var transition in latestTransitions
                         .OrderBy(static item => item.OccurredAtUtc)
                         .ThenBy(static item => item.Event.Version))
            {
                var current = transition.Event;
                _eventsById[current.EventId] = current;
                if (current.LifecycleStatus != AssetHealthEventLifecycleStatus.Active)
                    continue;

                if (!_assetStates.TryGetValue(current.AssetId, out var state) ||
                    state.ActiveEvent is null ||
                    state.ActiveEvent.LastObservedUtc <= current.LastObservedUtc)
                {
                    _assetStates[current.AssetId] = new TrackedAssetState(current.AssetId)
                    {
                        ActiveEvent = current,
                        LastEvaluatedUtc = current.LastObservedUtc,
                        LastJournaledUtc = transition.OccurredAtUtc
                    };
                }
            }

            _lastEvaluationUtc = latestTransitions.Count == 0
                ? null
                : latestTransitions.Max(static item => item.OccurredAtUtc);
            _lastError = null;
        }
    }

    public AssetHealthEventSnapshot? GetEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return null;
        lock (_stateGate)
            return _eventsById.GetValueOrDefault(eventId.Trim());
    }

    public IReadOnlyList<AssetHealthEventSnapshot> GetEvents(
        AssetHealthEventLifecycleStatus? lifecycleStatus = null,
        AssetHealthGrade? minimumGrade = null,
        int maximumCount = 200)
    {
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumEventsQueryCount);
        lock (_stateGate)
        {
            return _eventsById.Values
                .Where(item => lifecycleStatus is null || item.LifecycleStatus == lifecycleStatus)
                .Where(item => minimumGrade is null || IsAtLeast(item.Grade, minimumGrade.Value))
                .OrderByDescending(static item => item.LifecycleStatus == AssetHealthEventLifecycleStatus.Active)
                .ThenByDescending(static item => (int)item.Grade)
                .ThenByDescending(static item => item.LastObservedUtc)
                .Take(maximumCount)
                .ToArray();
        }
    }

    public AssetHealthGovernanceStatus GetStatus()
    {
        lock (_stateGate)
        {
            var active = _eventsById.Values
                .Where(static item => item.LifecycleStatus == AssetHealthEventLifecycleStatus.Active)
                .ToArray();
            return new AssetHealthGovernanceStatus
            {
                Enabled = _options.Enabled,
                HealthScoringEnabled = _healthOptions.Enabled,
                MesPushEnabled = _options.MesPushEnabled,
                TrackedAssets = _assetStates.Count,
                RetainedEvents = _eventsById.Count,
                ActiveEvents = active.Length,
                AcknowledgedActiveEvents = active.Count(static item => item.Acknowledged),
                SuppressedActiveEvents = active.Count(static item => item.IsSuppressed),
                MinimumEventGrade = _options.MinimumEventGrade,
                ConsecutiveUnhealthyEvaluations = _options.ConsecutiveUnhealthyEvaluations,
                ConsecutiveRecoveryEvaluations = _options.ConsecutiveRecoveryEvaluations,
                EvaluationIntervalSeconds = _options.EvaluationIntervalSeconds,
                LastEvaluationUtc = _lastEvaluationUtc,
                LastError = _lastError
            };
        }
    }

    public async ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        utcNow = NormalizeUtc(utcNow);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            List<ExpiredSuppression> expired;
            lock (_stateGate)
            {
                expired = _assetStates.Values
                    .Where(static state => state.ActiveEvent is not null)
                    .Select(static state => (state.AssetId, Event: state.ActiveEvent!))
                    .Where(item => IsSuppressionExpired(item.Event, utcNow))
                    .Select(item =>
                    {
                        var updated = item.Event with
                        {
                            Version = item.Event.Version + 1,
                            IsSuppressed = false,
                            SuppressedUntilUtc = null,
                            SuppressedReason = null,
                            LastObservedUtc = utcNow
                        };
                        return new ExpiredSuppression(
                            item.AssetId,
                            updated,
                            CreateTransition(
                                updated,
                                AssetHealthEventTransitionType.Unsuppressed,
                                utcNow,
                                "system",
                                "Suppression window expired."));
                    })
                    .ToList();
            }

            foreach (var item in expired)
                await _journal.AppendAsync(item.Transition, cancellationToken);

            lock (_stateGate)
            {
                foreach (var item in expired)
                {
                    if (_assetStates.TryGetValue(item.AssetId, out var state))
                    {
                        state.ActiveEvent = item.Event;
                        state.LastJournaledUtc = utcNow;
                    }
                    _eventsById[item.Event.EventId] = item.Event;
                }

                var inactiveCutoff = utcNow.AddSeconds(-_options.InactiveStateRetentionSeconds);
                foreach (var assetId in _assetStates
                             .Where(pair => pair.Value.ActiveEvent is null &&
                                            pair.Value.LastEvaluatedUtc < inactiveCutoff)
                             .Select(static pair => pair.Key)
                             .ToArray())
                    _assetStates.Remove(assetId);

                var eventCutoff = utcNow.AddHours(-_options.EventRetentionHours);
                foreach (var eventId in _eventsById
                             .Where(pair =>
                                 pair.Value.LifecycleStatus == AssetHealthEventLifecycleStatus.Recovered &&
                                 pair.Value.RecoveredAtUtc is not null &&
                                 pair.Value.RecoveredAtUtc < eventCutoff)
                             .Select(static pair => pair.Key)
                             .ToArray())
                    _eventsById.Remove(eventId);
            }

            await _journal.MaintainAsync(utcNow, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<AssetHealthEventSnapshot?> MutateEventAsync(
        string eventId,
        DateTime utcNow,
        CancellationToken cancellationToken,
        Func<AssetHealthEventSnapshot, EventMutation?> mutation)
    {
        utcNow = NormalizeUtc(utcNow);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            AssetHealthEventSnapshot current;
            lock (_stateGate)
            {
                if (!_eventsById.TryGetValue(eventId, out var found) ||
                    found.LifecycleStatus != AssetHealthEventLifecycleStatus.Active)
                    return null;
                current = found;
            }

            var result = mutation(current);
            if (result is null) return current;

            var updated = result.Event with { LastObservedUtc = utcNow };
            var transition = CreateTransition(
                updated,
                result.Type,
                utcNow,
                result.Actor,
                result.Note);
            await _journal.AppendAsync(transition, cancellationToken);

            lock (_stateGate)
            {
                _eventsById[eventId] = updated;
                if (_assetStates.TryGetValue(updated.AssetId, out var state) &&
                    state.ActiveEvent?.EventId == eventId)
                {
                    state.ActiveEvent = updated;
                    state.LastJournaledUtc = utcNow;
                }
                _lastError = null;
            }
            return updated;
        }
        catch (Exception exception)
        {
            lock (_stateGate) _lastError = exception.Message;
            throw;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private AssetHealthEventSnapshot CreateRaisedEvent(
        AssetHealthScoreSnapshot snapshot,
        DateTime utcNow)
    {
        var factor = ResolvePrimaryFactor(snapshot);
        return new AssetHealthEventSnapshot
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventKey = snapshot.AssetId.Trim(),
            AssetId = snapshot.AssetId.Trim(),
            Version = 1,
            LifecycleStatus = AssetHealthEventLifecycleStatus.Active,
            Grade = snapshot.Grade,
            PeakGrade = snapshot.Grade,
            HealthScore = snapshot.HealthScore,
            LowestHealthScore = snapshot.HealthScore,
            FirstDetectedUtc = utcNow,
            LastObservedUtc = utcNow,
            RecoveredAtUtc = null,
            Acknowledged = false,
            AcknowledgedAtUtc = null,
            AcknowledgedBy = null,
            IsSuppressed = false,
            SuppressedUntilUtc = null,
            SuppressedReason = null,
            Reason = factor.Reason,
            Source = factor.Source,
            Category = factor.Category
        };
    }

    private AssetHealthEventTransition CreateTransition(
        AssetHealthEventSnapshot snapshot,
        AssetHealthEventTransitionType type,
        DateTime utcNow,
        string? actor,
        string? note)
    {
        var deliveryStatus = ResolveDeliveryStatus(snapshot, type);
        return new AssetHealthEventTransition
        {
            Sequence = 0,
            MessageId = CreateMessageId(snapshot.EventId, snapshot.Version, type),
            TransitionType = type,
            Event = snapshot,
            OccurredAtUtc = utcNow,
            Actor = NormalizeOptional(actor),
            Note = NormalizeOptional(note),
            DeliveryStatus = deliveryStatus,
            DeliveryAttemptCount = 0,
            NextDeliveryAttemptUtc = deliveryStatus == AssetHealthDeliveryStatus.Pending ? utcNow : null,
            LastDeliveryAttemptUtc = null,
            DeliveredAtUtc = null,
            LastHttpStatusCode = null,
            LastDeliveryError = null
        };
    }

    private AssetHealthDeliveryStatus ResolveDeliveryStatus(
        AssetHealthEventSnapshot snapshot,
        AssetHealthEventTransitionType type)
    {
        if (!_options.MesPushEnabled || type == AssetHealthEventTransitionType.Observed)
            return AssetHealthDeliveryStatus.Disabled;
        if (snapshot.IsSuppressed && type == AssetHealthEventTransitionType.GradeChanged)
            return AssetHealthDeliveryStatus.Suppressed;
        return AssetHealthDeliveryStatus.Pending;
    }

    private bool TryMakeTrackingCapacity()
    {
        if (_assetStates.Count < _options.MaximumTrackedAssets) return true;
        var candidate = _assetStates.Values
            .Where(static state => state.ActiveEvent is null)
            .OrderBy(static state => state.LastEvaluatedUtc)
            .FirstOrDefault();
        if (candidate is null)
        {
            _lastError = "Maximum tracked health-governance assets reached; all tracked assets have active events.";
            return false;
        }
        _assetStates.Remove(candidate.AssetId);
        return true;
    }

    private static (string Source, string Category, string Reason) ResolvePrimaryFactor(
        AssetHealthScoreSnapshot snapshot)
    {
        var factor = snapshot.Factors
            .OrderByDescending(static item => item.Penalty)
            .ThenByDescending(static item => item.Contribution)
            .FirstOrDefault();
        return factor is null
            ? ("Fusion", "AssetHealth", snapshot.Summary)
            : (factor.Source, factor.Category, factor.Reason);
    }

    private static bool IsAtLeast(AssetHealthGrade value, AssetHealthGrade minimum) =>
        (int)value >= (int)minimum;

    private static bool IsMoreSevere(AssetHealthGrade value, AssetHealthGrade other) =>
        (int)value > (int)other;

    private static bool IsSuppressionExpired(AssetHealthEventSnapshot snapshot, DateTime utcNow) =>
        snapshot.IsSuppressed &&
        snapshot.SuppressedUntilUtc is not null &&
        snapshot.SuppressedUntilUtc <= utcNow;

    private static string CreateMessageId(
        string eventId,
        int version,
        AssetHealthEventTransitionType type)
    {
        var raw = $"{eventId}|{version}|{(int)type}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static DateTime NormalizeUtc(DateTime value) => value == default
        ? DateTime.UtcNow
        : value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record EventMutation(
        AssetHealthEventSnapshot Event,
        AssetHealthEventTransitionType Type,
        string? Actor,
        string? Note);

    private sealed record ExpiredSuppression(
        string AssetId,
        AssetHealthEventSnapshot Event,
        AssetHealthEventTransition Transition);

    private sealed class TrackedAssetState
    {
        public TrackedAssetState(string assetId)
        {
            AssetId = assetId;
        }

        public string AssetId { get; }
        public int ConsecutiveUnhealthy { get; set; }
        public int ConsecutiveRecovery { get; set; }
        public DateTime LastEvaluatedUtc { get; set; }
        public DateTime LastJournaledUtc { get; set; }
        public AssetHealthEventSnapshot? ActiveEvent { get; set; }

        public TrackedAssetState Clone() => new(AssetId)
        {
            ConsecutiveUnhealthy = ConsecutiveUnhealthy,
            ConsecutiveRecovery = ConsecutiveRecovery,
            LastEvaluatedUtc = LastEvaluatedUtc,
            LastJournaledUtc = LastJournaledUtc,
            ActiveEvent = ActiveEvent
        };
    }
}
