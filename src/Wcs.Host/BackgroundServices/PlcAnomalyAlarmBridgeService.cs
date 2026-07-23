namespace Wcs.Host.BackgroundServices;

using Wcs.Core.AlarmCenter;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

/// <summary>只有正式激活的异常才进入 AlarmCenter；观察级异常只保留在异常历史中。</summary>
public sealed class PlcAnomalyAlarmBridgeService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly IAlarmCenter _alarmCenter;
    private readonly PlcAnomalyOptions _options;
    private readonly ILogger<PlcAnomalyAlarmBridgeService> _logger;

    public PlcAnomalyAlarmBridgeService(
        IEventBus eventBus,
        IAlarmCenter alarmCenter,
        PlcAnomalyOptions options,
        ILogger<PlcAnomalyAlarmBridgeService> logger)
    {
        _eventBus = eventBus;
        _alarmCenter = alarmCenter;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        _eventBus.Subscribe<PlcAnomalyDetectedEvent>(async (evt, ct) =>
        {
            var anomaly = evt.Anomaly;
            if (!anomaly.RaiseAlarm || anomaly.Severity == PlcAnomalySeverity.Observe) return;

            var level = MapLevel(anomaly.Severity);
            _alarmCenter.SetAlarmRule(new AlarmRule
            {
                AlarmCode = anomaly.AlarmCode,
                Level = level,
                DelayRaiseMs = Math.Max(0, _options.AlarmDelayRaiseMs),
                DelayRecoverMs = Math.Max(0, _options.AlarmDelayRecoverMs),
                SuppressionWindowSec = 60,
                SuppressionThreshold = 100,
                AlarmGroup = $"PLC_ANOMALY:{anomaly.DeviceId}",
                AutoRecover = true
            });

            await _alarmCenter.RaiseAlarmAsync(
                anomaly.AlarmCode,
                level,
                anomaly.Reason,
                source: "PlcAnomalyEngine",
                deviceId: anomaly.DeviceId,
                alarmGroup: $"PLC_ANOMALY:{anomaly.DeviceId}",
                ct: ct);
        });

        _eventBus.Subscribe<PlcAnomalyRecoveredEvent>(async (evt, ct) =>
        {
            if (!evt.Anomaly.RaiseAlarm || evt.Anomaly.Severity == PlcAnomalySeverity.Observe) return;
            await _alarmCenter.RecoverAlarmAsync(evt.Anomaly.AlarmCode, ct);
        });

        _logger.LogInformation("PLC anomaly AlarmCenter bridge started");
        return Task.CompletedTask;
    }

    private static AlarmLevelEnum MapLevel(PlcAnomalySeverity severity) => severity switch
    {
        PlcAnomalySeverity.Observe => AlarmLevelEnum.Info,
        PlcAnomalySeverity.Warning => AlarmLevelEnum.Warning,
        PlcAnomalySeverity.Error => AlarmLevelEnum.Error,
        PlcAnomalySeverity.Critical => AlarmLevelEnum.Critical,
        _ => AlarmLevelEnum.Warning
    };
}
