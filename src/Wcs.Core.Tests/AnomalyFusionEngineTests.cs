namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;

public sealed class AnomalyFusionEngineTests
{
    [Fact]
    public void Same_source_uses_only_strongest_evidence_and_cannot_reach_alarm_alone()
    {
        var engine = new AnomalyFusionEngine(CreateOptions(
            consecutiveWarning: 1,
            consecutiveAlarm: 1,
            consecutiveRecovery: 1));
        var now = Utc(0);

        engine.Process(Evidence(
            "RULE-1",
            AnomalyEvidenceSources.ThresholdRule,
            "CV-01",
            score: 0.90,
            confidence: 0.95,
            observedAtUtc: now));
        engine.Process(Evidence(
            "RULE-2",
            AnomalyEvidenceSources.ThresholdRule,
            "CV-01",
            score: 0.99,
            confidence: 0.95,
            observedAtUtc: now.AddSeconds(1)));

        var snapshot = Assert.NotNull(engine.GetAsset("CV-01"));
        Assert.Equal(FusedHealthStatus.Warning, snapshot.Status);
        Assert.Equal(1, snapshot.IndependentSourceCount);
        Assert.Single(snapshot.Evidence);
        Assert.Equal("RULE-2", snapshot.Evidence[0].EvidenceId);
        Assert.InRange(snapshot.Score, 0.9404, 0.9406);
        Assert.Equal(2, engine.GetStatus().EvidenceAccepted);
        Assert.Equal(0, engine.GetStatus().AlarmTransitions);
    }

    [Fact]
    public void Independent_sources_require_consecutive_evaluations_before_alarm()
    {
        var engine = new AnomalyFusionEngine(CreateOptions(
            consecutiveWarning: 1,
            consecutiveAlarm: 2,
            consecutiveRecovery: 2));
        var now = Utc(0);

        engine.Process(Evidence(
            "RULE-1",
            AnomalyEvidenceSources.ConsistencyRule,
            "RGV-01",
            score: 0.92,
            confidence: 0.98,
            observedAtUtc: now,
            severity: PlcAnomalySeverity.Error));
        Assert.Equal(FusedHealthStatus.Warning, engine.GetAsset("RGV-01")!.Status);

        var ml = Evidence(
            "ML-1",
            AnomalyEvidenceSources.IsolationForest,
            "RGV-01",
            score: 0.78,
            confidence: 0.82,
            observedAtUtc: now.AddSeconds(1));
        engine.Process(ml);

        var firstAlarmEvaluation = Assert.NotNull(engine.GetAsset("RGV-01"));
        Assert.Equal(FusedHealthStatus.Warning, firstAlarmEvaluation.Status);
        Assert.Equal(2, firstAlarmEvaluation.IndependentSourceCount);
        Assert.True(firstAlarmEvaluation.Score >= 0.85);

        engine.Process(ml with { ObservedAtUtc = now.AddSeconds(2) });
        var alarm = Assert.NotNull(engine.GetAsset("RGV-01"));
        Assert.Equal(FusedHealthStatus.Alarm, alarm.Status);
        Assert.Equal(2, alarm.IndependentSourceCount);
        Assert.Equal(1, engine.GetStatus().AlarmTransitions);
    }

    [Fact]
    public void Alarm_recovery_requires_all_sources_clear_and_consecutive_low_evaluations()
    {
        var engine = new AnomalyFusionEngine(CreateOptions(
            consecutiveWarning: 1,
            consecutiveAlarm: 1,
            consecutiveRecovery: 2));
        var now = Utc(0);
        var rule = Evidence(
            "RULE-1",
            AnomalyEvidenceSources.ConsistencyRule,
            "EMS-01",
            0.95,
            0.98,
            now,
            severity: PlcAnomalySeverity.Error);
        var cycle = Evidence(
            "CYCLE-1",
            AnomalyEvidenceSources.CyclePhaseDuration,
            "EMS-01",
            0.90,
            0.90,
            now.AddSeconds(1));

        engine.Process(rule);
        engine.Process(cycle);
        Assert.Equal(FusedHealthStatus.Alarm, engine.GetAsset("EMS-01")!.Status);

        engine.Process(rule with
        {
            State = AnomalyEvidenceState.Recovered,
            ObservedAtUtc = now.AddSeconds(2)
        });
        Assert.Equal(FusedHealthStatus.Alarm, engine.GetAsset("EMS-01")!.Status);
        Assert.Equal(1, engine.GetAsset("EMS-01")!.IndependentSourceCount);

        var recoveredCycle = cycle with
        {
            State = AnomalyEvidenceState.Recovered,
            ObservedAtUtc = now.AddSeconds(3)
        };
        engine.Process(recoveredCycle);
        Assert.Equal(FusedHealthStatus.Alarm, engine.GetAsset("EMS-01")!.Status);
        Assert.Equal(0, engine.GetAsset("EMS-01")!.IndependentSourceCount);

        engine.Process(recoveredCycle with { ObservedAtUtc = now.AddSeconds(4) });
        var normal = Assert.NotNull(engine.GetAsset("EMS-01"));
        Assert.Equal(FusedHealthStatus.Normal, normal.Status);
        Assert.Equal(0, normal.Score);
        Assert.Empty(normal.Evidence);
        Assert.Equal(1, engine.GetStatus().RecoveryTransitions);
    }

    [Fact]
    public void Active_evidence_expires_and_asset_is_eventually_evicted()
    {
        var options = CreateOptions(
            consecutiveWarning: 1,
            consecutiveAlarm: 1,
            consecutiveRecovery: 1);
        options.EvidenceRetentionSeconds = 1;
        options.InactiveStateRetentionSeconds = 2;
        var engine = new AnomalyFusionEngine(options);
        var now = Utc(0);

        engine.Process(Evidence(
            "PEER-1",
            AnomalyEvidenceSources.PeerMedianMad,
            "CV-02",
            0.80,
            0.85,
            now,
            expiresAtUtc: now.AddSeconds(1)));
        Assert.NotNull(engine.GetAsset("CV-02"));

        engine.Maintenance(now.AddSeconds(2));
        var recovered = Assert.NotNull(engine.GetAsset("CV-02"));
        Assert.Equal(FusedHealthStatus.Normal, recovered.Status);
        Assert.Empty(recovered.Evidence);
        Assert.Equal(1, engine.GetStatus().EvidenceExpired);

        engine.Maintenance(now.AddSeconds(5));
        Assert.Null(engine.GetAsset("CV-02"));
        Assert.Equal(0, engine.GetStatus().TrackedAssets);
    }

    [Fact]
    public void Asset_capacity_is_bounded_and_excess_evidence_is_dropped()
    {
        var options = CreateOptions(1, 1, 1);
        options.MaximumTrackedAssets = 2;
        var engine = new AnomalyFusionEngine(options);
        var now = Utc(0);

        engine.Process(Evidence("A", AnomalyEvidenceSources.ThresholdRule, "A", 0.8, 0.9, now));
        engine.Process(Evidence("B", AnomalyEvidenceSources.ThresholdRule, "B", 0.8, 0.9, now));
        engine.Process(Evidence("C", AnomalyEvidenceSources.ThresholdRule, "C", 0.8, 0.9, now));

        var status = engine.GetStatus();
        Assert.Equal(2, status.TrackedAssets);
        Assert.Equal(1, status.EvidenceDropped);
        Assert.NotNull(engine.GetAsset("A"));
        Assert.NotNull(engine.GetAsset("B"));
        Assert.Null(engine.GetAsset("C"));
    }

    [Fact]
    public void Disabled_fusion_records_no_state()
    {
        var options = CreateOptions(1, 1, 1);
        options.Enabled = false;
        var engine = new AnomalyFusionEngine(options);

        engine.Process(Evidence(
            "OFF",
            AnomalyEvidenceSources.ThresholdRule,
            "CV-OFF",
            1,
            1,
            Utc(0)));

        Assert.Null(engine.GetAsset("CV-OFF"));
        Assert.Equal(0, engine.GetStatus().TrackedAssets);
        Assert.Equal(0, engine.GetStatus().EvidenceAccepted);
    }

    private static AnomalyFusionOptions CreateOptions(
        int consecutiveWarning,
        int consecutiveAlarm,
        int consecutiveRecovery) => new()
    {
        Enabled = true,
        EvidenceRetentionSeconds = 300,
        RecoveredEvidenceRetentionSeconds = 60,
        InactiveStateRetentionSeconds = 600,
        MaximumTrackedAssets = 100,
        MaximumEvidencePerAsset = 20,
        MaximumSnapshots = 100,
        ObserveThreshold = 0.35,
        WarningThreshold = 0.65,
        AlarmThreshold = 0.85,
        RecoveryThreshold = 0.25,
        MinimumIndependentSourcesForAlarm = 2,
        ConsecutiveWarningEvaluations = consecutiveWarning,
        ConsecutiveAlarmEvaluations = consecutiveAlarm,
        ConsecutiveRecoveryEvaluations = consecutiveRecovery,
        SourceDiversityBonus = 0.05,
        MaximumSourceDiversityBonus = 0.15
    };

    private static AnomalyEvidence Evidence(
        string evidenceId,
        string source,
        string assetId,
        double score,
        double confidence,
        DateTime observedAtUtc,
        PlcAnomalySeverity severity = PlcAnomalySeverity.Warning,
        DateTime? expiresAtUtc = null) => new()
    {
        EvidenceId = evidenceId,
        Source = source,
        AssetId = assetId,
        Category = source,
        State = AnomalyEvidenceState.Active,
        ObservedAtUtc = observedAtUtc,
        ExpiresAtUtc = expiresAtUtc,
        Score = score,
        Confidence = confidence,
        Severity = severity,
        Reason = $"evidence {evidenceId}"
    };

    private static DateTime Utc(int seconds) =>
        new(2026, 1, 1, 0, 0, seconds, DateTimeKind.Utc);
}
