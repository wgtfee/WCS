namespace Wcs.Core.AlarmCenter;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.AlarmCenter.Engine;
using Wcs.Core.AlarmCenter.Masking;
using Wcs.Core.AlarmCenter.Models;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警中心接口
/// </summary>
public interface IAlarmCenter
{
    /// <summary>
    /// 原始报警信号到达 — 经防抖→风暴抑制→状态机→聚合后产生实际报警
    /// </summary>
    Task RaiseAlarmAsync(string alarmCode, AlarmLevelEnum level, string message,
        string? source = null, string? deviceId = null, string? alarmGroup = null,
        CancellationToken ct = default);

    /// <summary>
    /// 原始恢复信号到达 — 经防抖→状态机→聚合后恢复报警
    /// </summary>
    Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default);

    /// <summary>
    /// 确认报警（仅 Active 状态可用）
    /// </summary>
    Task AcknowledgeAlarmAsync(string alarmId, CancellationToken ct = default);

    /// <summary>
    /// 获取报警状态
    /// </summary>
    AlarmState? GetAlarm(string alarmId);

    /// <summary>
    /// 获取所有活跃报警（含 PendingRecover）
    /// </summary>
    IEnumerable<AlarmState> GetActiveAlarms();

    /// <summary>
    /// 按级别过滤报警
    /// </summary>
    IEnumerable<AlarmState> GetAlarmsByLevel(AlarmLevelEnum level);

    /// <summary>
    /// 获取活跃报警数
    /// </summary>
    int GetActiveCount();

    /// <summary>
    /// 注册/更新报警规则
    /// </summary>
    void SetAlarmRule(AlarmRule rule);

    /// <summary>
    /// 是否处于风暴模式
    /// </summary>
    bool IsInStormMode { get; }

    /// <summary>
    /// 按时间范围查询报警历史
    /// </summary>
    IEnumerable<AlarmState> GetAlarmsByTimeRange(DateTime from, DateTime to);

    /// <summary>
    /// 按报警代码+时间范围查询
    /// </summary>
    IEnumerable<AlarmState> GetAlarmsByCode(string alarmCode, DateTime from, DateTime to);

    /// <summary>
    /// 获取报警总数（含已恢复）
    /// </summary>
    int GetTotalCount();

    /// <summary>
    /// 获取从指定报警到根因的路径
    /// </summary>
    IReadOnlyList<string> GetRootCausePath(string alarmId);

    /// <summary>
    /// 获取指定设备的所有根因报警
    /// </summary>
    IEnumerable<AlarmState> GetDeviceRootAlarms(string deviceId);

    /// <summary>
    /// 获取报警在根因树中的深度
    /// </summary>
    int GetRootCauseDepth(string alarmId);
}

/// <summary>
/// 报警中心实现 — 5 层报警管线：
/// Raw Signal → AlarmDebounceEngine → AlarmStormGuard → AlarmStateMachine
///   → AlarmAggregationEngine → EventBus
/// </summary>
public class AlarmCenter : IAlarmCenter, ISnapshotProvider
{
    private readonly ConcurrentDictionary<string, AlarmState> _alarms = new();       // alarmId → state
    private readonly ConcurrentDictionary<string, AlarmRule> _rules = new();          // alarmCode → rule
    private readonly IEventBus _eventBus;
    private readonly AlarmDebounceEngine _debounceEngine;
    private readonly AlarmStormGuard _stormGuard;
    private readonly AlarmAggregationEngine _aggregation;
    private readonly AlarmMaskManager _maskManager;

    public bool IsInStormMode => _stormGuard.IsInStormMode;

    /// <summary>
    /// 默认规则 — 给未注册的 AlarmCode 使用
    /// </summary>
    private static readonly AlarmRule DefaultRule = new()
    {
        AlarmCode = "*",
        Level = AlarmLevelEnum.Warning,
        DelayRaiseMs = 1000,
        DelayRecoverMs = 3000,
        SuppressionWindowSec = 60,
        SuppressionThreshold = 10
    };

    private static readonly AlarmRule ErrorRule = new()
    {
        AlarmCode = "*",
        Level = AlarmLevelEnum.Error,
        DelayRaiseMs = 1000,
        DelayRecoverMs = 3000,
        SuppressionWindowSec = 60,
        SuppressionThreshold = 10
    };

    public AlarmCenter(IEventBus eventBus, AlarmMaskManager? maskManager = null)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _maskManager = maskManager ?? new AlarmMaskManager();

        _aggregation = new AlarmAggregationEngine();

        _stormGuard = new AlarmStormGuard(windowSeconds: 60, globalMaxPerWindow: 1000);
        _stormGuard.StormStarted += () => OnStormEvent(true);
        _stormGuard.StormEnded += () => OnStormEvent(false);

        _debounceEngine = new AlarmDebounceEngine(
            onConfirmedRaise: OnDebounceConfirmedRaise,
            onConfirmedRecover: OnDebounceConfirmedRecover,
            onCanceledRaise: OnDebounceCanceledRaise,
            onRebounce: OnDebounceRebounce
        );
    }

    // ==================== 规则管理 ====================

    public void SetAlarmRule(AlarmRule rule)
    {
        _rules[rule.AlarmCode] = rule;
    }

    private AlarmRule GetRule(string alarmCode)
    {
        return _rules.TryGetValue(alarmCode, out var rule) ? rule : DefaultRule;
    }

    // ==================== 报警信号入口 ====================

    public Task RaiseAlarmAsync(string alarmCode, AlarmLevelEnum level, string message,
        string? source = null, string? deviceId = null, string? alarmGroup = null,
        CancellationToken ct = default)
    {
        // Step 1: 查找规则
        var rule = GetRule(alarmCode);

        // Step 2: 防抖 — 信号进入 DelayRaise 窗口
        _debounceEngine.SignalRaise(alarmCode, rule);

        return Task.CompletedTask;
    }

    public Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default)
    {
        // Step 1: 查找规则
        var rule = GetRule(alarmCode);

        // Step 2: 防抖 — 信号进入 DelayRecover 窗口
        _debounceEngine.SignalRecover(alarmCode, rule);

        return Task.CompletedTask;
    }

    // ==================== 防抖回调 ====================

    /// <summary>
    /// DelayRaise 到期 — 确认报警
    /// </summary>
    private void OnDebounceConfirmedRaise(string alarmCode)
    {
        var rule = GetRule(alarmCode);

        // Step 3: 风暴检测
        if (!_stormGuard.CheckAndCount(alarmCode, rule))
        {
            // 被风暴抑制，不产生报警实体
            return;
        }

        // Step 3b: 屏蔽检测（设备维修等场景）
        if (_maskManager.IsMasked(rule.AlarmGroup, alarmCode))
        {
            return; // 被屏蔽，不产生报警
        }

        var alarmId = $"ALM-{alarmCode}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var alarm = new AlarmState
        {
            AlarmId = alarmId,
            AlarmCode = alarmCode,
            Status = AlarmStatusEnum.Active,
            Level = rule.Level,
            Message = $"[WCS] {alarmCode}",
            OccurTime = DateTime.UtcNow
        };
        _alarms[alarmId] = alarm;

        // Step 4: 聚合检测
        bool isRoot = _aggregation.RegisterAlarm(alarmId, alarmCode, rule.AlarmGroup);
        if (!isRoot)
        {
            // 是子报警，不在面板上显示
            alarm.Status = AlarmStatusEnum.Recovered;
        }

        // 发布事件
        _eventBus.PublishAsync(new AlarmRaisedEvent
        {
            AlarmId = alarmId,
            AlarmCode = alarmCode,
            Level = rule.Level,
            Message = alarmCode
        });
    }

    /// <summary>
    /// DelayRecover 到期 — 确认恢复
    /// </summary>
    private void OnDebounceConfirmedRecover(string alarmCode)
    {
        // 找到所有该 alarmCode 的活跃报警
        var toRecover = _alarms.Values
            .Where(a => a.AlarmCode == alarmCode && AlarmStateMachine.IsActive(a.Status))
            .ToList();

        foreach (var alarm in toRecover)
        {
            // 确保经过 PendingRecover → Recovered 合法路径
            if (alarm.Status != AlarmStatusEnum.PendingRecover)
                AlarmStateMachine.Transition(alarm, AlarmStatusEnum.PendingRecover);
            AlarmStateMachine.Transition(alarm, AlarmStatusEnum.Recovered);
            alarm.RecoverTime = DateTime.UtcNow;

            // 如果是根因，释放子报警
            _aggregation.RecoverGroup(alarm.AlarmId);
        }

        // 发布事件
        _eventBus.PublishAsync(new AlarmRecoveredEvent
        {
            AlarmCode = alarmCode,
            RecoverTime = DateTime.UtcNow
        });
    }

    /// <summary>
    /// PendingRaise 期间信号消失 — 取消报警
    /// </summary>
    private void OnDebounceCanceledRaise(string alarmCode)
    {
        // 无需产生报警记录
    }

    /// <summary>
    /// PendingRecover 期间信号重新触发 — 回到 Active
    /// </summary>
    private void OnDebounceRebounce(string alarmCode)
    {
        foreach (var alarm in _alarms.Values
            .Where(a => a.AlarmCode == alarmCode && a.Status == AlarmStatusEnum.PendingRecover))
        {
            AlarmStateMachine.Transition(alarm, AlarmStatusEnum.Active);
        }
    }

    // ==================== 用户操作 ====================

    public Task AcknowledgeAlarmAsync(string alarmId, CancellationToken ct = default)
    {
        if (_alarms.TryGetValue(alarmId, out var alarm))
        {
            if (alarm.Status == AlarmStatusEnum.Active)
            {
                AlarmStateMachine.Transition(alarm, AlarmStatusEnum.Acknowledged);
            }
        }
        return Task.CompletedTask;
    }

    // ==================== 查询 ====================

    public AlarmState? GetAlarm(string alarmId)
    {
        _alarms.TryGetValue(alarmId, out var state);
        return state;
    }

    public IEnumerable<AlarmState> GetActiveAlarms()
    {
        return _alarms.Values
            .Where(a => AlarmStateMachine.IsVisible(a.Status))
            .ToList();
    }

    public IEnumerable<AlarmState> GetAlarmsByLevel(AlarmLevelEnum level)
    {
        return _alarms.Values.Where(a => a.Level == level).ToList();
    }

    public int GetActiveCount()
    {
        return _alarms.Values.Count(a =>
            a.Status == AlarmStatusEnum.Active ||
            a.Status == AlarmStatusEnum.Acknowledged);
    }

    // ==================== 风暴事件 ====================

    private void OnStormEvent(bool started)
    {
        _eventBus.PublishAsync(new AlarmRaisedEvent
        {
            AlarmId = "SYSTEM-ALARMSTORM",
            AlarmCode = "ALARM_STORM",
            Level = AlarmLevelEnum.Critical,
            Message = started ? "Alarm storm detected — suppression active" : "Alarm storm ended — suppression released"
        });
    }

    // ==================== 批量查询（Item 5） ====================

    public IEnumerable<AlarmState> GetAlarmsByTimeRange(DateTime from, DateTime to)
    {
        return _alarms.Values
            .Where(a => a.OccurTime >= from && a.OccurTime <= to)
            .ToList();
    }

    public IEnumerable<AlarmState> GetAlarmsByCode(string alarmCode, DateTime from, DateTime to)
    {
        return _alarms.Values
            .Where(a => a.AlarmCode == alarmCode && a.OccurTime >= from && a.OccurTime <= to)
            .ToList();
    }

    public int GetTotalCount() => _alarms.Count;

    // ==================== 根因树查询（Phase 3） ====================

    public IReadOnlyList<string> GetRootCausePath(string alarmId)
    {
        return _aggregation.GetRootCausePath(alarmId);
    }

    public IEnumerable<AlarmState> GetDeviceRootAlarms(string deviceId)
    {
        return _alarms.Values
            .Where(a => a.RootCauseAlarmId == null  // 根因报警
                     && a.AlarmCode == deviceId     // 匹配设备
                     && AlarmStateMachine.IsVisible(a.Status))
            .ToList();
    }

    public int GetRootCauseDepth(string alarmId)
    {
        return _aggregation.GetRootCauseDepth(alarmId);
    }

    // ==================== ISnapshotProvider ====================

    public string ModuleName => "AlarmCenter";
    public int RestoreOrder => 2;

    public Task<object> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new AlarmCenterSnapshot
        {
            Alarms = _alarms.Values.ToList(),
            Rules = _rules.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
        return Task.FromResult<object>(snapshot);
    }

    public Task RestoreSnapshotAsync(object snapshot, CancellationToken ct = default)
    {
        List<AlarmState>? alarms = null;
        Dictionary<string, AlarmRule>? rules = null;

        if (snapshot is JsonElement element)
        {
            var parsed = JsonSerializer.Deserialize<AlarmCenterSnapshot>(element.GetRawText());
            if (parsed != null)
            {
                alarms = parsed.Alarms;
                rules = parsed.Rules;
            }
        }
        else if (snapshot is AlarmCenterSnapshot s)
        {
            alarms = s.Alarms;
            rules = s.Rules;
        }

        if (alarms != null)
        {
            _alarms.Clear();
            foreach (var alarm in alarms)
            {
                // 折叠中间态：PendingRaise → Active, PendingRecover → Recovered
                var status = alarm.Status switch
                {
                    AlarmStatusEnum.PendingRaise => AlarmStatusEnum.Active,
                    AlarmStatusEnum.PendingRecover => AlarmStatusEnum.Recovered,
                    _ => alarm.Status
                };
                alarm.Status = status;
                _alarms[alarm.AlarmId] = alarm;
            }
        }

        if (rules != null)
        {
            _rules.Clear();
            foreach (var kvp in rules)
                _rules[kvp.Key] = kvp.Value;
        }

        // 重置瞬态引擎状态
        _aggregation.Clear();
        _stormGuard.Reset();

        return Task.CompletedTask;
    }

    // ==================== 清理 ====================

    public void Dispose()
    {
        _debounceEngine.Dispose();
        _aggregation.Clear();
        _stormGuard.Reset();
    }
}

/// <summary>
/// 报警中心快照 — 用于 ISnapshotProvider 序列化
/// </summary>
public class AlarmCenterSnapshot
{
    public List<AlarmState> Alarms { get; set; } = new();
    public Dictionary<string, AlarmRule> Rules { get; set; } = new();
}
