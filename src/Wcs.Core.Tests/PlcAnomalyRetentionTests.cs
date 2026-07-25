namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcAnomalyRetentionTests
{
    [Fact]
    public async Task Threshold_only_states_use_no_statistical_windows_and_are_evicted()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "THRESHOLD-ONLY",
            SignalPattern = "*_Current",
            Maximum = 10,
            RaiseAlarm = false
        });
        var engine = new PlcAnomalyEngine(options, new EventBus());
        var now = DateTime.UtcNow;

        for (var index = 0; index < 500; index++)
            await engine.ProcessAsync(Numeric($"CV{index:D4}_Current", 5, now));

        var before = engine.GetStatus();
        Assert.Equal(500, before.TrackedRuleSignals);
        Assert.Equal(0, before.StatisticalWindows);
        Assert.Equal(0, before.TrackedDeviceSnapshots);
        Assert.Equal(0, before.TrackedRelatedSamples);

        await engine.SweepAsync(now.AddSeconds(2));

        var after = engine.GetStatus();
        Assert.Equal(0, after.TrackedRuleSignals);
        Assert.Equal(500, after.EvictedRuleStates);
    }

    [Fact]
    public async Task Consistency_snapshots_and_inactive_states_are_evicted_together()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "RUNNING-WITHOUT-SPEED",
            SignalPattern = "*_Running",
            RelatedSignalPattern = "*_Speed",
            WhenValueEquals = "true",
            RelatedMinimum = 0.1,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            RaiseAlarm = false
        });
        var engine = new PlcAnomalyEngine(options, new EventBus());
        var now = DateTime.UtcNow;

        for (var index = 0; index < 50; index++)
        {
            await engine.ProcessAsync(Numeric($"CV{index:D3}_Speed", 2, now));
            await engine.ProcessAsync(Boolean($"CV{index:D3}_Running", false, now.AddMilliseconds(10)));
        }

        var before = engine.GetStatus();
        Assert.Equal(50, before.TrackedRuleSignals);
        Assert.Equal(50, before.TrackedDeviceSnapshots);
        Assert.Equal(100, before.TrackedRelatedSamples);

        await engine.SweepAsync(now.AddSeconds(2));

        var after = engine.GetStatus();
        Assert.Equal(0, after.TrackedRuleSignals);
        Assert.Equal(0, after.TrackedDeviceSnapshots);
        Assert.Equal(0, after.TrackedRelatedSamples);
        Assert.Equal(50, after.EvictedRuleStates);
        Assert.Equal(100, after.EvictedRelatedSamples);
        Assert.Equal(50, after.EvictedDeviceSnapshots);
    }

    [Fact]
    public async Task Active_anomaly_is_never_evicted_but_recovered_state_expires()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "ACTIVE-THRESHOLD",
            SignalPattern = "CV01_Current",
            Maximum = 10,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            RaiseAlarm = false
        });
        var engine = new PlcAnomalyEngine(options, new EventBus());
        var now = DateTime.UtcNow;

        await engine.ProcessAsync(Numeric("CV01_Current", 20, now));
        await engine.SweepAsync(now.AddHours(1));

        var active = engine.GetStatus();
        Assert.Equal(1, active.ActiveAnomalies);
        Assert.Equal(1, active.TrackedRuleSignals);
        Assert.Equal(0, active.EvictedRuleStates);

        await engine.ProcessAsync(Numeric("CV01_Current", 5, now.AddHours(1).AddMilliseconds(10)));
        await engine.SweepAsync(now.AddHours(1).AddSeconds(2));

        var recovered = engine.GetStatus();
        Assert.Equal(0, recovered.ActiveAnomalies);
        Assert.Equal(0, recovered.TrackedRuleSignals);
        Assert.Equal(1, recovered.EvictedRuleStates);
    }

    [Fact]
    public async Task Statistical_window_is_allocated_only_for_mad_rule()
    {
        var options = new PlcAnomalyOptions
        {
            Enabled = true,
            WindowSize = 32,
            MinimumSamples = 3,
            InactiveStateRetentionSeconds = 1,
            RelatedSampleRetentionSeconds = 1,
            MaximumCleanupItemsPerSweep = 10_000,
            Rules = new List<PlcAnomalyRule>
            {
                new()
                {
                    RuleId = "PLAIN",
                    SignalPattern = "CV01_Current",
                    Maximum = 10,
                    RaiseAlarm = false
                },
                new()
                {
                    RuleId = "MAD",
                    SignalPattern = "CV02_Current",
                    StatisticalBaselineEnabled = true,
                    RaiseAlarm = false
                }
            }
        };
        var engine = new PlcAnomalyEngine(options, new EventBus());
        var now = DateTime.UtcNow;

        await engine.ProcessAsync(Numeric("CV01_Current", 5, now));
        await engine.ProcessAsync(Numeric("CV02_Current", 5, now));

        var status = engine.GetStatus();
        Assert.Equal(2, status.TrackedRuleSignals);
        Assert.Equal(1, status.StatisticalWindows);
    }

    private static PlcAnomalyOptions CreateOptions(PlcAnomalyRule rule) => new()
    {
        Enabled = true,
        WindowSize = 32,
        MinimumSamples = 3,
        MaximumTrackedRuleSignals = 10_000,
        InactiveStateRetentionSeconds = 1,
        RelatedSampleRetentionSeconds = 1,
        MaximumCleanupItemsPerSweep = 10_000,
        ConsecutiveWarningCount = 1,
        ConsecutiveAlarmCount = 1,
        RecoveryCount = 1,
        Rules = new List<PlcAnomalyRule> { rule }
    };

    private static PlcAnomalySample Numeric(string signal, double value, DateTime timestampUtc) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestampUtc,
        PlcName = "PLC-TEST",
        DbBlock = 1,
        DeviceId = signal.Split('_')[0],
        SignalName = signal,
        NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NumericValue = value
    };

    private static PlcAnomalySample Boolean(string signal, bool value, DateTime timestampUtc) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestampUtc,
        PlcName = "PLC-TEST",
        DbBlock = 1,
        DeviceId = signal.Split('_')[0],
        SignalName = signal,
        NewValue = value.ToString(),
        BooleanValue = value
    };
}
