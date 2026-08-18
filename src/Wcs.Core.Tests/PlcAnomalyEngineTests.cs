namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcAnomalyEngineTests
{
    [Fact]
    public async Task Threshold_requires_consecutive_hits_and_recovers_after_normal_samples()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "CURRENT-HIGH",
            SignalPattern = "CV01_Current",
            Maximum = 10,
            ConsecutiveAbnormalCount = 3,
            ConsecutiveRecoveryCount = 2,
            RaiseAlarm = false
        });
        var (engine, detected, recovered) = CreateEngine(options);
        var now = DateTime.UtcNow;

        await engine.ProcessAsync(Sample("CV01_Current", 11, now));
        await engine.ProcessAsync(Sample("CV01_Current", 12, now.AddMilliseconds(100)));
        Assert.Empty(detected);

        await engine.ProcessAsync(Sample("CV01_Current", 13, now.AddMilliseconds(200)));
        Assert.Single(detected);
        Assert.Single(engine.GetActiveAnomalies());

        await engine.ProcessAsync(Sample("CV01_Current", 5, now.AddMilliseconds(300)));
        Assert.Empty(recovered);
        await engine.ProcessAsync(Sample("CV01_Current", 5, now.AddMilliseconds(400)));

        Assert.Single(recovered);
        Assert.Empty(engine.GetActiveAnomalies());
        Assert.Equal(1, engine.GetStatus().Raised);
        Assert.Equal(1, engine.GetStatus().Recovered);
    }

    [Fact]
    public async Task Median_mad_detects_outlier_without_learning_the_fault_value()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "DYNAMIC-CURRENT",
            SignalPattern = "CV02_Current",
            StatisticalBaselineEnabled = true,
            MadMultiplier = 5,
            MinimumMad = 0.05,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            RaiseAlarm = false
        });
        options.MinimumSamples = 10;
        var (engine, detected, recovered) = CreateEngine(options);
        var now = DateTime.UtcNow;

        for (var index = 0; index < 10; index++)
            await engine.ProcessAsync(Sample("CV02_Current", 10 + (index % 3 - 1) * 0.1, now.AddSeconds(index)));

        await engine.ProcessAsync(Sample("CV02_Current", 30, now.AddSeconds(11)));

        var anomaly = Assert.Single(detected).Anomaly;
        Assert.Equal(PlcAnomalyType.StatisticalBaseline, anomaly.Type);
        Assert.Equal("MedianMadDetector", anomaly.DetectorName);
        Assert.True(anomaly.Score >= 0.85);
        Assert.InRange(anomaly.ExpectedValue!.Value, 9.8, 10.2);

        await engine.ProcessAsync(Sample("CV02_Current", 10, now.AddSeconds(12)));
        Assert.Single(recovered);
    }

    [Fact]
    public async Task Duration_sweep_detects_signal_that_has_no_second_edge()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "BUSY-TIMEOUT",
            SignalPattern = "CV03_Busy",
            MaximumTrueDurationMs = 1_000,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            RaiseAlarm = false
        });
        var (engine, detected, recovered) = CreateEngine(options);
        var now = DateTime.UtcNow;

        await engine.ProcessAsync(BooleanSample("CV03_Busy", true, now));
        await engine.SweepAsync(now.AddMilliseconds(1_500));

        var anomaly = Assert.Single(detected).Anomaly;
        Assert.Equal(PlcAnomalyType.Duration, anomaly.Type);
        Assert.Single(engine.GetActiveAnomalies());

        await engine.ProcessAsync(BooleanSample("CV03_Busy", false, now.AddMilliseconds(1_600)));
        Assert.Single(recovered);
        Assert.Empty(engine.GetActiveAnomalies());
    }

    [Fact]
    public async Task Rate_detector_uses_elapsed_time_and_ignores_normal_rate()
    {
        var options = CreateOptions(new PlcAnomalyRule
        {
            RuleId = "SPEED-RATE",
            SignalPattern = "CV04_Speed",
            MaximumRatePerSecond = 5,
            ConsecutiveAbnormalCount = 1,
            ConsecutiveRecoveryCount = 1,
            RaiseAlarm = false
        });
        var (engine, detected, recovered) = CreateEngine(options);
        var now = DateTime.UtcNow;

        await engine.ProcessAsync(Sample("CV04_Speed", 0, now));
        await engine.ProcessAsync(Sample("CV04_Speed", 2, now.AddSeconds(1)));
        Assert.Empty(detected);

        await engine.ProcessAsync(Sample("CV04_Speed", 20, now.AddSeconds(2)));
        Assert.Equal(PlcAnomalyType.RateOfChange, Assert.Single(detected).Anomaly.Type);

        await engine.ProcessAsync(Sample("CV04_Speed", 22, now.AddSeconds(3)));
        Assert.Single(recovered);
    }

    private static PlcAnomalyOptions CreateOptions(PlcAnomalyRule rule) => new()
    {
        Enabled = true,
        WindowSize = 100,
        MinimumSamples = 10,
        ObserveThreshold = 0.70,
        WarningThreshold = 0.85,
        AlarmThreshold = 0.95,
        ConsecutiveWarningCount = 3,
        ConsecutiveAlarmCount = 5,
        RecoveryCount = 2,
        Rules = new List<PlcAnomalyRule> { rule }
    };

    private static (
        PlcAnomalyEngine Engine,
        ConcurrentBag<PlcAnomalyDetectedEvent> Detected,
        ConcurrentBag<PlcAnomalyRecoveredEvent> Recovered) CreateEngine(PlcAnomalyOptions options)
    {
        var eventBus = new EventBus();
        var detected = new ConcurrentBag<PlcAnomalyDetectedEvent>();
        var recovered = new ConcurrentBag<PlcAnomalyRecoveredEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
            return Task.CompletedTask;
        });
        eventBus.Subscribe<PlcAnomalyRecoveredEvent>((evt, _) =>
        {
            recovered.Add(evt);
            return Task.CompletedTask;
        });
        return (new PlcAnomalyEngine(options, eventBus), detected, recovered);
    }

    private static PlcAnomalySample Sample(string signal, double value, DateTime timestampUtc) => new()
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

    private static PlcAnomalySample BooleanSample(string signal, bool value, DateTime timestampUtc) => new()
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
