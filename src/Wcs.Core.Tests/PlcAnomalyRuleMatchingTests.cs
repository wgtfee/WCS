namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcAnomalyRuleMatchingTests
{
    [Fact]
    public async Task Load_test_wildcards_match_plc_device_and_signal()
    {
        var eventBus = new EventBus();
        var detected = new List<PlcAnomalyDetectedEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
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
                    RuleId = "ANOMALY-LOAD-HIGH-CURRENT",
                    Enabled = true,
                    PlcPattern = "ANOMALY-LOAD-PLC",
                    DevicePattern = "ANOM*",
                    SignalPattern = "*_ANOMALY_LOAD_Current",
                    Maximum = 10,
                    ConsecutiveAbnormalCount = 1,
                    RaiseAlarm = false
                }
            }
        }, eventBus);

        await engine.ProcessAsync(new PlcAnomalySample
        {
            EventId = Guid.NewGuid().ToString("N"),
            TimestampUtc = DateTime.UtcNow,
            PlcName = "ANOMALY-LOAD-PLC",
            DbBlock = 900,
            DeviceId = "ANOMCV000001",
            SignalName = "ANOMCV000001_ANOMALY_LOAD_Current",
            NewValue = "20",
            NumericValue = 20
        });

        Assert.Single(detected);
        var status = engine.GetStatus();
        Assert.Equal(1, status.ConfiguredRules);
        Assert.Equal(1, status.MatchedRuleEvaluations);
        Assert.Equal(1, status.TrackedRuleSignals);
    }
}
