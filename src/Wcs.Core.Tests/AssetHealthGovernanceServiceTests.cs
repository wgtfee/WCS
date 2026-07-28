namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetHealthGovernanceServiceTests
{
    [Fact]
    public async Task Event_is_raised_only_after_consecutive_unhealthy_evaluations()
    {
        var store = new FakeJournalStore();
        var service = CreateService(store, unhealthy: 3, recovery: 2);
        var start = DateTime.UnixEpoch;

        Assert.Empty(await service.EvaluateAsync(Snapshot("RGV-01", 60, AssetHealthGrade.Degraded), start));
        Assert.Empty(await service.EvaluateAsync(Snapshot("RGV-01", 59, AssetHealthGrade.Degraded), start.AddSeconds(10)));
        var transitions = await service.EvaluateAsync(
            Snapshot("RGV-01", 58, AssetHealthGrade.Degraded),
            start.AddSeconds(20));

        var raised = Assert.Single(transitions);
        Assert.Equal(AssetHealthEventTransitionType.Raised, raised.TransitionType);
        Assert.Equal(AssetHealthDeliveryStatus.Pending, raised.DeliveryStatus);
        Assert.Equal(58, raised.Event.HealthScore);
        Assert.Single(service.GetEvents(AssetHealthEventLifecycleStatus.Active));
        Assert.Single(store.Transitions);
    }

    [Fact]
    public async Task Active_event_records_grade_change_and_recovers_after_hysteresis()
    {
        var store = new FakeJournalStore();
        var service = CreateService(store, unhealthy: 1, recovery: 2);
        var start = DateTime.UnixEpoch;

        var raised = Assert.Single(await service.EvaluateAsync(
            Snapshot("EMS-01", 60, AssetHealthGrade.Degraded), start));
        var changed = Assert.Single(await service.EvaluateAsync(
            Snapshot("EMS-01", 30, AssetHealthGrade.Critical), start.AddSeconds(10)));
        Assert.Equal(AssetHealthEventTransitionType.GradeChanged, changed.TransitionType);
        Assert.Equal(AssetHealthGrade.Critical, changed.Event.PeakGrade);
        Assert.Equal(30, changed.Event.LowestHealthScore);

        Assert.Empty(await service.EvaluateAsync(
            Snapshot("EMS-01", 90, AssetHealthGrade.Healthy), start.AddSeconds(20)));
        var recovered = Assert.Single(await service.EvaluateAsync(
            Snapshot("EMS-01", 92, AssetHealthGrade.Healthy), start.AddSeconds(30)));

        Assert.Equal(AssetHealthEventTransitionType.Recovered, recovered.TransitionType);
        Assert.Equal(AssetHealthEventLifecycleStatus.Recovered, recovered.Event.LifecycleStatus);
        Assert.Equal(AssetHealthDeliveryStatus.Pending, recovered.DeliveryStatus);
        Assert.Empty(service.GetEvents(AssetHealthEventLifecycleStatus.Active));
        Assert.Equal(raised.Event.EventId, recovered.Event.EventId);
    }

    [Fact]
    public async Task Acknowledge_suppress_and_unsuppress_are_audited_versions()
    {
        var store = new FakeJournalStore();
        var service = CreateService(store, unhealthy: 1, recovery: 2);
        var start = DateTime.UnixEpoch;
        var raised = Assert.Single(await service.EvaluateAsync(
            Snapshot("CV-01", 55, AssetHealthGrade.Degraded), start));

        var acknowledged = await service.AcknowledgeAsync(
            raised.Event.EventId, "operator-a", "checked", start.AddSeconds(1));
        Assert.NotNull(acknowledged);
        Assert.True(acknowledged!.Acknowledged);
        Assert.Equal("operator-a", acknowledged.AcknowledgedBy);

        var suppressed = await service.SuppressAsync(
            raised.Event.EventId,
            "operator-b",
            "planned maintenance",
            start.AddHours(1),
            start.AddSeconds(2));
        Assert.NotNull(suppressed);
        Assert.True(suppressed!.IsSuppressed);

        var unsuppressed = await service.UnsuppressAsync(
            raised.Event.EventId, "operator-b", "maintenance complete", start.AddSeconds(3));
        Assert.NotNull(unsuppressed);
        Assert.False(unsuppressed!.IsSuppressed);
        Assert.Equal(4, unsuppressed.Version);
        Assert.Equal(
            new[]
            {
                AssetHealthEventTransitionType.Raised,
                AssetHealthEventTransitionType.Acknowledged,
                AssetHealthEventTransitionType.Suppressed,
                AssetHealthEventTransitionType.Unsuppressed
            },
            store.Transitions.Select(static item => item.TransitionType));
    }

    [Fact]
    public async Task Grade_change_during_suppression_is_journaled_but_not_pushed()
    {
        var store = new FakeJournalStore();
        var service = CreateService(store, unhealthy: 1, recovery: 2);
        var start = DateTime.UnixEpoch;
        var raised = Assert.Single(await service.EvaluateAsync(
            Snapshot("RGV-02", 60, AssetHealthGrade.Degraded), start));
        await service.SuppressAsync(
            raised.Event.EventId,
            "operator",
            "known test",
            start.AddHours(1),
            start.AddSeconds(1));

        var changed = Assert.Single(await service.EvaluateAsync(
            Snapshot("RGV-02", 20, AssetHealthGrade.Critical), start.AddSeconds(2)));
        Assert.Equal(AssetHealthEventTransitionType.GradeChanged, changed.TransitionType);
        Assert.Equal(AssetHealthDeliveryStatus.Suppressed, changed.DeliveryStatus);
    }

    [Fact]
    public async Task Restore_recovers_active_events_without_replaying_delivery()
    {
        var store = new FakeJournalStore();
        var first = CreateService(store, unhealthy: 1, recovery: 2);
        var raised = Assert.Single(await first.EvaluateAsync(
            Snapshot("EMS-RESTORE", 50, AssetHealthGrade.Degraded), DateTime.UnixEpoch));

        var secondStore = new FakeJournalStore();
        var second = CreateService(secondStore, unhealthy: 1, recovery: 2);
        second.Restore(new[] { raised });

        var restored = Assert.Single(second.GetEvents(AssetHealthEventLifecycleStatus.Active));
        Assert.Equal(raised.Event.EventId, restored.EventId);
        Assert.Empty(secondStore.Transitions);
    }

    private static AssetHealthGovernanceService CreateService(
        FakeJournalStore store,
        int unhealthy,
        int recovery)
    {
        var governance = new AssetHealthGovernanceOptions
        {
            Enabled = true,
            MinimumEventGrade = AssetHealthGrade.Degraded,
            ConsecutiveUnhealthyEvaluations = unhealthy,
            ConsecutiveRecoveryEvaluations = recovery,
            EvaluationIntervalSeconds = 10,
            MaximumUnchangedEventIntervalSeconds = 300,
            MaximumTrackedAssets = 100,
            MaximumEventsQueryCount = 100,
            InactiveStateRetentionSeconds = 3600,
            EventRetentionHours = 24,
            MesPushEnabled = true
        };
        var health = new AssetHealthScoringOptions { Enabled = true };
        return new AssetHealthGovernanceService(governance, health, store);
    }

    private static AssetHealthScoreSnapshot Snapshot(
        string assetId,
        double score,
        AssetHealthGrade grade) => new()
    {
        AssetId = assetId,
        HealthScore = score,
        Grade = grade,
        FusionRiskScore = Math.Clamp((100 - score) / 100, 0, 1),
        FusionStatus = grade switch
        {
            AssetHealthGrade.Healthy => FusedHealthStatus.Normal,
            AssetHealthGrade.Attention => FusedHealthStatus.Observe,
            AssetHealthGrade.Degraded => FusedHealthStatus.Warning,
            _ => FusedHealthStatus.Alarm
        },
        IndependentSourceCount = grade >= AssetHealthGrade.Degraded ? 2 : 0,
        CalculatedAtUtc = DateTime.UnixEpoch,
        Factors = new[]
        {
            new AssetHealthFactor
            {
                Source = "PLC",
                Category = "Current",
                Contribution = 0.8,
                Penalty = 20,
                Reason = "Motor current deviation."
            }
        },
        Summary = $"{assetId}:{score:F2}"
    };

    private sealed class FakeJournalStore : IAssetHealthEventJournalStore
    {
        public List<AssetHealthEventTransition> Transitions { get; } = new();
        public string Provider => "Fake";

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> AppendAsync(
            AssetHealthEventTransition transition,
            CancellationToken cancellationToken = default)
        {
            if (Transitions.Any(item => item.MessageId == transition.MessageId))
                return ValueTask.FromResult(false);
            Transitions.Add(transition with { Sequence = Transitions.Count + 1 });
            return ValueTask.FromResult(true);
        }

        public ValueTask<IReadOnlyList<AssetHealthEventTransition>> LoadLatestAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(
                Transitions
                    .GroupBy(static item => item.Event.EventId)
                    .Select(static group => group.OrderByDescending(item => item.Event.Version).First())
                    .Take(maximumCount)
                    .ToArray());

        public ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetHistoryAsync(
            string eventId,
            int maximumCount = 200,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(
                Transitions.Where(item => item.Event.EventId == eventId).TakeLast(maximumCount).ToArray());

        public ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetPendingDeliveriesAsync(
            DateTime utcNow,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(
                Transitions
                    .Where(item => item.DeliveryStatus is AssetHealthDeliveryStatus.Pending or AssetHealthDeliveryStatus.Retrying)
                    .Take(maximumCount)
                    .ToArray());

        public ValueTask MarkDeliveredAsync(
            string messageId,
            DateTime deliveredAtUtc,
            int? httpStatusCode,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask MarkDeliveryFailedAsync(
            string messageId,
            int attemptCount,
            DateTime nextAttemptUtc,
            bool deadLetter,
            int? httpStatusCode,
            string error,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> RetryDeliveryAsync(
            string messageId,
            DateTime utcNow,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<AssetHealthEventJournalStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AssetHealthEventJournalStatus
            {
                Enabled = true,
                Provider = Provider,
                IsAvailable = true,
                RetainedTransitions = Transitions.Count,
                RetainedEvents = Transitions.Select(static item => item.Event.EventId).Distinct().Count(),
                PendingDeliveries = Transitions.Count(static item => item.DeliveryStatus == AssetHealthDeliveryStatus.Pending),
                RetryingDeliveries = 0,
                DeliveredMessages = 0,
                DeadLetterMessages = 0,
                LastSuccessfulWriteUtc = null,
                LastSuccessfulDeliveryUtc = null,
                LastError = null
            });

        public ValueTask MaintainAsync(
            DateTime utcNow,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
