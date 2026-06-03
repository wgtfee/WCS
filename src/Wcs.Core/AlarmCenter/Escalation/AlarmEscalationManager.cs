namespace Wcs.Core.AlarmCenter.Escalation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wcs.Core.AlarmCenter.Models;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警升级管理器 — 监控活跃报警，超时未处理则逐级上报
///
/// 典型配置：
/// 级别1: 1分钟未确认 → 通知班长（Notify）
/// 级别2: 5分钟未确认 → 通知主管（Notify）
/// 级别3: 10分钟未确认 → 停线（StopLine）
/// </summary>
public sealed class AlarmEscalationManager : IDisposable
{
    private readonly ConcurrentDictionary<string, AlarmEscalationRule> _rules = new();
    private readonly ConcurrentDictionary<string, EscalationTracker> _trackers = new(); // alarmId → tracker
    private readonly IEventBus _eventBus;
    private readonly ILogger<AlarmEscalationManager>? _logger;
    private readonly Timer _checkTimer;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(10);
    private bool _disposed;

    /// <summary>
    /// 单个报警的升级追踪
    /// </summary>
    private class EscalationTracker
    {
        public string AlarmId { get; set; } = string.Empty;
        public string AlarmCode { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public AlarmLevelEnum Level { get; set; }
        public DateTime OccurTime { get; set; }
        public bool IsAcknowledged { get; set; }
        public int CurrentEscalationLevel { get; set; }
        public HashSet<int> TriggeredLevels { get; set; } = new();
    }

    public AlarmEscalationManager(
        IEventBus eventBus,
        ILogger<AlarmEscalationManager>? logger = null)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _logger = logger;
        _checkTimer = new Timer(CheckEscalations, null, _checkInterval, _checkInterval);
    }

    /// <summary>
    /// 注册升级规则
    /// </summary>
    public void RegisterRule(AlarmEscalationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules[rule.RuleId] = rule;
    }

    /// <summary>
    /// 移除升级规则
    /// </summary>
    public bool RemoveRule(string ruleId) => _rules.TryRemove(ruleId, out _);

    /// <summary>
    /// 跟踪报警（当报警产生时调用）
    /// </summary>
    public void TrackAlarm(string alarmId, string alarmCode, AlarmLevelEnum level, string? deviceId = null)
    {
        _trackers[alarmId] = new EscalationTracker
        {
            AlarmId = alarmId,
            AlarmCode = alarmCode,
            DeviceId = deviceId,
            Level = level,
            OccurTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 确认报警（取消升级）
    /// </summary>
    public void AcknowledgeAlarm(string alarmId)
    {
        if (_trackers.TryGetValue(alarmId, out var tracker))
        {
            tracker.IsAcknowledged = true;
            _logger?.LogInformation("Escalation: alarm {AlarmId} acknowledged — escalation cancelled", alarmId);
        }
    }

    /// <summary>
    /// 报警恢复后移除追踪
    /// </summary>
    public void RemoveAlarm(string alarmId)
    {
        _trackers.TryRemove(alarmId, out _);
    }

    /// <summary>
    /// 获取升级状态
    /// </summary>
    public IReadOnlyList<EscalationStatus> GetActiveEscalations()
    {
        return _trackers.Values
            .Where(t => !t.IsAcknowledged)
            .Select(t => new EscalationStatus
            {
                AlarmId = t.AlarmId,
                AlarmCode = t.AlarmCode,
                DeviceId = t.DeviceId,
                Elapsed = DateTime.UtcNow - t.OccurTime,
                CurrentLevel = t.CurrentEscalationLevel,
                IsAcknowledged = t.IsAcknowledged
            })
            .ToList();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _checkTimer.Dispose();
        }
    }

    private void CheckEscalations(object? state)
    {
        var now = DateTime.UtcNow;

        foreach (var (alarmId, tracker) in _trackers)
        {
            if (tracker.IsAcknowledged)
                continue;

            var elapsed = now - tracker.OccurTime;

            foreach (var rule in _rules.Values)
            {
                if (!rule.Enabled) continue;
                if (rule.AlarmCode != null && rule.AlarmCode != tracker.AlarmCode) continue;
                if (rule.DeviceId != null && rule.DeviceId != tracker.DeviceId) continue;
                if (tracker.Level < rule.MinLevel) continue;

                // 检查每个升级级别
                foreach (var escalationLevel in rule.Levels.OrderBy(l => l.Level))
                {
                    if (tracker.TriggeredLevels.Contains(escalationLevel.Level))
                        continue;

                    if (elapsed >= escalationLevel.Delay)
                    {
                        TriggerEscalation(tracker, escalationLevel);
                        tracker.CurrentEscalationLevel = escalationLevel.Level;
                        tracker.TriggeredLevels.Add(escalationLevel.Level);
                    }
                }
            }
        }
    }

    private void TriggerEscalation(EscalationTracker tracker, EscalationLevel level)
    {
        _logger?.LogWarning(
            "ESCALATION [{Level}] Alarm {AlarmId} ({AlarmCode}) on {DeviceId} — {ActionType} -> {Target}",
            level.Level, tracker.AlarmId, tracker.AlarmCode, tracker.DeviceId,
            level.ActionType, level.NotifyTarget);

        // 发布升级事件
        _eventBus.PublishAsync(new AlarmEscalatedEvent
        {
            AlarmId = tracker.AlarmId,
            AlarmCode = tracker.AlarmCode,
            DeviceId = tracker.DeviceId ?? "",
            EscalationLevel = level.Level,
            ActionType = level.ActionType,
            NotifyTarget = level.NotifyTarget,
            Message = $"Alarm {tracker.AlarmCode} escalated to level {level.Level} " +
                      $"({level.ActionType} -> {level.NotifyTarget})"
        });
    }
}

/// <summary>
/// 报警升级状态（供查询）
/// </summary>
public class EscalationStatus
{
    public string AlarmId { get; set; } = string.Empty;
    public string AlarmCode { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int CurrentLevel { get; set; }
    public bool IsAcknowledged { get; set; }
}

/// <summary>
/// 报警升级事件
/// </summary>
public class AlarmEscalatedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Critical;

    public string AlarmId { get; set; } = string.Empty;
    public string AlarmCode { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int EscalationLevel { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string NotifyTarget { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
