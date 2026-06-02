namespace Wcs.Core.AlarmCenter;

using Wcs.Core.StateCenter.Models;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 报警中心接口
/// </summary>
public interface IAlarmCenter
{
    /// <summary>
    /// 产生报警
    /// </summary>
    Task RaiseAlarmAsync(string alarmCode, AlarmLevelEnum level, string message, string? source = null, CancellationToken ct = default);

    /// <summary>
    /// 确认报警
    /// </summary>
    Task AcknowledgeAlarmAsync(string alarmId, CancellationToken ct = default);

    /// <summary>
    /// 恢复报警
    /// </summary>
    Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default);

    /// <summary>
    /// 获取报警状态
    /// </summary>
    AlarmState? GetAlarm(string alarmId);

    /// <summary>
    /// 获取所有活跃报警
    /// </summary>
    IEnumerable<AlarmState> GetActiveAlarms();

    /// <summary>
    /// 按级别过滤报警
    /// </summary>
    IEnumerable<AlarmState> GetAlarmsByLevel(AlarmLevelEnum level);

    /// <summary>
    /// 获取报警总数
    /// </summary>
    int GetActiveCount();
}

/// <summary>
/// 报警中心实现 - 统一管理报警产生、确认、恢复
/// </summary>
public class AlarmCenter : IAlarmCenter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AlarmState> _alarms = new();
    private readonly IEventBus _eventBus;

    public AlarmCenter(IEventBus eventBus)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    public async Task RaiseAlarmAsync(string alarmCode, AlarmLevelEnum level, string message, string? source = null, CancellationToken ct = default)
    {
        var alarmId = $"ALM-{alarmCode}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var alarm = new AlarmState
        {
            AlarmId = alarmId,
            AlarmCode = alarmCode,
            Status = AlarmStatusEnum.Active,
            Level = level,
            Message = $"[{source ?? "WCS"}] {message}",
            OccurTime = DateTime.UtcNow
        };

        _alarms[alarmId] = alarm;

        await _eventBus.PublishAsync(new AlarmRaisedEvent
        {
            AlarmId = alarmId,
            AlarmCode = alarmCode,
            Level = level,
            Message = message,
            AlarmState = alarm
        }, ct);
    }

    public Task AcknowledgeAlarmAsync(string alarmId, CancellationToken ct = default)
    {
        if (_alarms.TryGetValue(alarmId, out var alarm) && alarm.Status == AlarmStatusEnum.Active)
        {
            alarm.Status = AlarmStatusEnum.Acknowledged;
        }
        return Task.CompletedTask;
    }

    public async Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default)
    {
        foreach (var kvp in _alarms)
        {
            if (kvp.Value.AlarmCode == alarmCode &&
                (kvp.Value.Status == AlarmStatusEnum.Active || kvp.Value.Status == AlarmStatusEnum.Acknowledged))
            {
                kvp.Value.Status = AlarmStatusEnum.Recovered;
                kvp.Value.RecoverTime = DateTime.UtcNow;

                await _eventBus.PublishAsync(new AlarmRecoveredEvent
                {
                    AlarmId = kvp.Key,
                    AlarmCode = alarmCode,
                    RecoverTime = kvp.Value.RecoverTime.Value
                }, ct);
            }
        }
    }

    public AlarmState? GetAlarm(string alarmId)
    {
        _alarms.TryGetValue(alarmId, out var state);
        return state;
    }

    public IEnumerable<AlarmState> GetActiveAlarms()
    {
        return _alarms.Values
            .Where(a => a.Status == AlarmStatusEnum.Active || a.Status == AlarmStatusEnum.Acknowledged)
            .ToList();
    }

    public IEnumerable<AlarmState> GetAlarmsByLevel(AlarmLevelEnum level)
    {
        return _alarms.Values.Where(a => a.Level == level).ToList();
    }

    public int GetActiveCount()
    {
        return _alarms.Values.Count(a => a.Status == AlarmStatusEnum.Active);
    }
}
