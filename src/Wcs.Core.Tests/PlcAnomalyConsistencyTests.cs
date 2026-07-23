namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcAnomalyConsistencyTests
{
    [Fact]
    public async Task Running_with_zero_speed_raises_and_speed_feedback_recovers()
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

        var engine = new PlcAnomalyEngine(new PlcAnomalyOptions
        {
            Enabled = true,
            ConsecutiveWarningCount = 1,
            ConsecutiveAlarmCount = 1,
            RecoveryCount = 1,
            Rules = new List<PlcAnomalyRule>
            {
                new()
                {
                    RuleId = "RUNNING-WITHOUT-SPEED",
                    SignalPattern = "*_Running",
                    RelatedSignalPattern = "*_Speed",
                    WhenValueEquals = "true",
                    RelatedMinimum = 0.1,
                    MaximumRelatedAgeMs = 5_000,
                    ConsecutiveAbnormalCount = 1,
                    ConsecutiveRecoveryCount = 1,
                    RaiseAlarm = false
                }
            }
        }, eventBus);

        var now = DateTime.UtcNow;
        await engine.ProcessAsync(Numeric("CV05_Speed", 0, now));
        await engine.ProcessAsync(Boolean("CV05_Running", true, now.AddMilliseconds(10)));

        var anomaly = Assert.Single(detected).Anomaly;
        Assert.Equal(PlcAnomalyType.Consistency, anomaly.Type);
        Assert.Equal("ConsistencyDetector", anomaly.DetectorName);
        Assert.Contains("不满足预期", anomaly.Reason);
        Assert.Single(engine.GetActiveAnomalies());

        await engine.ProcessAsync(Numeric("CV05_Speed", 2, now.AddMilliseconds(20)));

        Assert.Single(recovered);
        Assert.Empty(engine.GetActiveAnomalies());
        Assert.Equal(1, engine.GetStatus().Raised);
        Assert.Equal(1, engine.GetStatus().Recovered);
    }

    [Fact]
    public async Task Consistency_condition_false_does_not_raise()
    {
        var eventBus = new EventBus();
        var detected = new ConcurrentBag<PlcAnomalyDetectedEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
            return Task.CompletedTask;
        });

        var engine = new PlcAnomalyEngine(new PlcAnomalyOptions
        {
            Enabled = true,
            Rules = new List<PlcAnomalyRule>
            {
                new()
                {
                    RuleId = "RUNNING-WITHOUT-SPEED",
                    SignalPattern = "*_Running",
                    RelatedSignalPattern = "*_Speed",
                    WhenValueEquals = "true",
                    RelatedMinimum = 0.1,
                    ConsecutiveAbnormalCount = 1,
                    RaiseAlarm = false
                }
            }
        }, eventBus);

        var now = DateTime.UtcNow;
        await engine.ProcessAsync(Numeric("CV06_Speed", 0, now));
        await engine.ProcessAsync(Boolean("CV06_Running", false, now.AddMilliseconds(10)));

        Assert.Empty(detected);
        Assert.Empty(engine.GetActiveAnomalies());
    }

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
