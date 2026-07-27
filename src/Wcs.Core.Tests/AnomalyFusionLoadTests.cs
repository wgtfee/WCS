namespace Wcs.Core.Tests;

using System.Diagnostics;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;
using Xunit.Abstractions;

public sealed class AnomalyFusionLoadTests
{
    private readonly ITestOutputHelper _output;

    public AnomalyFusionLoadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "FusionLoad")]
    public void Million_evidence_lifecycle_is_bounded_and_fully_evicted()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WCS_RUN_FUSION_LOAD"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("Fusion load test skipped; set WCS_RUN_FUSION_LOAD=true to execute it.");
            return;
        }

        const int assetCount = 10_000;
        const int cyclesPerAsset = 25;
        const long expectedActiveEvents = (long)assetCount * cyclesPerAsset * 2;
        const long expectedRecoveredEvents = expectedActiveEvents;
        const long expectedTotalEvents = expectedActiveEvents + expectedRecoveredEvents;

        var options = new AnomalyFusionOptions
        {
            Enabled = true,
            EvidenceRetentionSeconds = 1,
            RecoveredEvidenceRetentionSeconds = 1,
            InactiveStateRetentionSeconds = 1,
            MaximumTrackedAssets = assetCount,
            MaximumEvidencePerAsset = 4,
            MaximumSnapshots = 2_000,
            ObserveThreshold = 0.35,
            WarningThreshold = 0.65,
            AlarmThreshold = 0.85,
            RecoveryThreshold = 0.25,
            MinimumIndependentSourcesForAlarm = 2,
            ConsecutiveWarningEvaluations = 1,
            ConsecutiveAlarmEvaluations = 1,
            ConsecutiveRecoveryEvaluations = 1,
            SourceDiversityBonus = 0.05,
            MaximumSourceDiversityBonus = 0.15
        };
        var engine = new AnomalyFusionEngine(options);
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ForceFullCollection();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;

        var stopwatch = Stopwatch.StartNew();
        long sequence = 0;
        for (var assetIndex = 0; assetIndex < assetCount; assetIndex++)
        {
            var assetId = $"FUSION-ASSET-{assetIndex:D5}";
            var ruleId = $"RULE|{assetIndex:D5}";
            var mlId = $"ML|{assetIndex:D5}";

            for (var cycle = 0; cycle < cyclesPerAsset; cycle++)
            {
                var observedAt = epoch.AddTicks(++sequence);
                var rule = Evidence(
                    ruleId,
                    AnomalyEvidenceSources.ConsistencyRule,
                    assetId,
                    observedAt,
                    score: 0.95,
                    confidence: 0.98,
                    severity: PlcAnomalySeverity.Error);
                var ml = Evidence(
                    mlId,
                    AnomalyEvidenceSources.IsolationForest,
                    assetId,
                    observedAt.AddTicks(1),
                    score: 0.90,
                    confidence: 0.90,
                    severity: PlcAnomalySeverity.Warning);

                engine.Process(rule);
                engine.Process(ml);
                engine.Process(rule with
                {
                    State = AnomalyEvidenceState.Recovered,
                    ObservedAtUtc = observedAt.AddTicks(2)
                });
                engine.Process(ml with
                {
                    State = AnomalyEvidenceState.Recovered,
                    ObservedAtUtc = observedAt.AddTicks(3)
                });
            }
        }
        stopwatch.Stop();

        var afterIngress = engine.GetStatus();
        Assert.Equal(expectedActiveEvents, afterIngress.EvidenceAccepted);
        Assert.Equal(expectedRecoveredEvents, afterIngress.EvidenceRecovered);
        Assert.Equal(expectedTotalEvents, afterIngress.Evaluations);
        Assert.Equal(assetCount, afterIngress.TrackedAssets);
        Assert.Equal(0, afterIngress.ActiveEvidence);
        Assert.Equal(0, afterIngress.EvidenceDropped);
        Assert.True(afterIngress.RetainedSnapshots <= options.MaximumSnapshots);

        // 第一次维护清除已恢复 Evidence；第二次维护淘汰空闲资产状态。
        engine.Maintenance(epoch.AddHours(1));
        engine.Maintenance(epoch.AddHours(1).AddSeconds(2));

        var finalStatus = engine.GetStatus();
        Assert.Equal(0, finalStatus.TrackedAssets);
        Assert.Equal(0, finalStatus.ActiveEvidence);
        Assert.Equal(assetCount, finalStatus.EvictedAssets);
        Assert.Equal(0, finalStatus.EvidenceDropped);

        ForceFullCollection();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        process.Refresh();
        var rssAfter = process.WorkingSet64;
        var managedGrowthMb = Math.Max(0, managedAfter - managedBefore) / 1024d / 1024d;
        var rssGrowthMb = Math.Max(0, rssAfter - rssBefore) / 1024d / 1024d;
        var eventsPerSecond = expectedTotalEvents / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);

        _output.WriteLine("events={0}", expectedTotalEvents);
        _output.WriteLine("assets={0}", assetCount);
        _output.WriteLine("cycles_per_asset={0}", cyclesPerAsset);
        _output.WriteLine("elapsed_ms={0}", stopwatch.ElapsedMilliseconds);
        _output.WriteLine("events_per_second={0:F2}", eventsPerSecond);
        _output.WriteLine("managed_before_bytes={0}", managedBefore);
        _output.WriteLine("managed_after_bytes={0}", managedAfter);
        _output.WriteLine("managed_growth_mb={0:F2}", managedGrowthMb);
        _output.WriteLine("rss_before_bytes={0}", rssBefore);
        _output.WriteLine("rss_after_bytes={0}", rssAfter);
        _output.WriteLine("rss_growth_mb={0:F2}", rssGrowthMb);
        _output.WriteLine("evicted_assets={0}", finalStatus.EvictedAssets);
        _output.WriteLine("retained_snapshots={0}", finalStatus.RetainedSnapshots);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMinutes(3),
            $"One million evidence events took {stopwatch.Elapsed}.");
        Assert.True(
            managedGrowthMb <= 192,
            $"Managed memory grew by {managedGrowthMb:F2} MB.");
        Assert.True(
            rssGrowthMb <= 250,
            $"RSS grew by {rssGrowthMb:F2} MB.");
    }

    private static AnomalyEvidence Evidence(
        string evidenceId,
        string source,
        string assetId,
        DateTime observedAtUtc,
        double score,
        double confidence,
        PlcAnomalySeverity severity) => new()
    {
        EvidenceId = evidenceId,
        Source = source,
        AssetId = assetId,
        Category = source,
        State = AnomalyEvidenceState.Active,
        ObservedAtUtc = observedAtUtc,
        Score = score,
        Confidence = confidence,
        Severity = severity,
        Reason = $"load evidence {evidenceId}"
    };

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
