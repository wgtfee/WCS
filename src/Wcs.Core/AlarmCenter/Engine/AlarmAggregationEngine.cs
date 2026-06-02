namespace Wcs.Core.AlarmCenter.Engine;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警聚合引擎 — 按 Device + AlarmGroup 做根因归并
/// 同一分组内最早到达的报警为根因（RootCause），其余子报警自动抑制
/// 根因恢复时释放所有子报警
/// </summary>
public sealed class AlarmAggregationEngine
{
    private readonly ConcurrentDictionary<AlarmGroupKey, string> _rootCauses = new(); // groupKey → rootAlarmId
    private readonly ConcurrentDictionary<string, HashSet<string>> _groupMembers = new(); // rootAlarmId → childIds
    private readonly ConcurrentDictionary<string, AlarmGroupKey> _alarmGroups = new(); // alarmId → groupKey

    /// <summary>
    /// 判断报警是否被抑制（作为子报警被根因抑制）
    /// </summary>
    public bool IsSuppressed(string alarmId)
    {
        return _alarmGroups.TryGetValue(alarmId, out var groupKey) &&
               _rootCauses.TryGetValue(groupKey, out var rootId) &&
               rootId != alarmId;
    }

    /// <summary>
    /// 获取根因报警 ID
    /// </summary>
    public string? GetRootCause(string alarmId)
    {
        if (!_alarmGroups.TryGetValue(alarmId, out var groupKey))
            return null;

        _rootCauses.TryGetValue(groupKey, out var rootId);
        return rootId;
    }

    /// <summary>
    /// 获取分组内所有子报警 ID
    /// </summary>
    public IReadOnlyCollection<string> GetGroupMembers(string rootAlarmId)
    {
        return _groupMembers.TryGetValue(rootAlarmId, out var members)
            ? members.ToList().AsReadOnly()
            : Array.Empty<string>();
    }

    /// <summary>
    /// 注册报警到聚合引擎 — 返回 true 表示该报警是根因，false 表示被子报警
    /// </summary>
    public bool RegisterAlarm(string alarmId, string deviceId, string? alarmGroup)
    {
        if (string.IsNullOrEmpty(alarmGroup))
            return true; // 无分组的不做聚合

        var groupKey = new AlarmGroupKey(deviceId, alarmGroup);
        _alarmGroups[alarmId] = groupKey;

        // 尝试设置根因（第一个到达的）
        if (_rootCauses.TryAdd(groupKey, alarmId))
        {
            _groupMembers[alarmId] = new HashSet<string>();
            return true; // 我是根因
        }

        // 已有根因 → 加入子报警列表
        var rootId = _rootCauses[groupKey];
        if (_groupMembers.TryGetValue(rootId, out var members))
        {
            lock (members)
            {
                members.Add(alarmId);
            }
        }
        return false; // 我是子报警
    }

    /// <summary>
    /// 恢复根因报警 — 返回该分组下所有被抑制的子报警 ID
    /// </summary>
    public IReadOnlyList<string> RecoverGroup(string rootAlarmId)
    {
        if (!_alarmGroups.TryGetValue(rootAlarmId, out var groupKey))
            return Array.Empty<string>();

        var released = new List<string>();

        if (_groupMembers.TryRemove(rootAlarmId, out var members))
        {
            lock (members)
            {
                released.AddRange(members);
            }
        }

        _rootCauses.TryRemove(groupKey, out _);

        // 清理成员索引
        foreach (var childId in released)
        {
            _alarmGroups.TryRemove(childId, out _);
        }
        _alarmGroups.TryRemove(rootAlarmId, out _);

        return released;
    }

    public void Clear()
    {
        _rootCauses.Clear();
        _groupMembers.Clear();
        _alarmGroups.Clear();
    }
}
