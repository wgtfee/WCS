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

    // 根因树层次结构
    private readonly ConcurrentDictionary<string, string?> _parentMap = new(); // alarmId → parentAlarmId (null=根)
    private readonly ConcurrentDictionary<string, HashSet<string>> _childrenMap = new(); // parentId → childIds
    private readonly ConcurrentDictionary<string, int> _depthMap = new(); // alarmId → depth

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

    /// <summary>
    /// 注册报警树层次关系 — 父子关系 + 深度计算
    /// </summary>
    /// <param name="alarmId">报警 ID</param>
    /// <param name="parentAlarmId">父报警 ID（null 表示根因）</param>
    public void RegisterAlarmHierarchy(string alarmId, string? parentAlarmId)
    {
        _parentMap[alarmId] = parentAlarmId;

        if (parentAlarmId != null)
        {
            var siblings = _childrenMap.GetOrAdd(parentAlarmId, _ => new HashSet<string>());
            lock (siblings) { siblings.Add(alarmId); }
            _depthMap[alarmId] = GetRootCauseDepthInternal(alarmId);
        }
        else
        {
            _depthMap[alarmId] = 0;
        }
    }

    /// <summary>
    /// 获取从当前报警到根因的路径（自底向上）
    /// </summary>
    public IReadOnlyList<string> GetRootCausePath(string alarmId)
    {
        var path = new List<string>();
        var current = alarmId;

        while (current != null)
        {
            path.Add(current);
            _parentMap.TryGetValue(current, out current);
        }

        return path.AsReadOnly();
    }

    /// <summary>
    /// 获取指定报警的所有后代报警 ID（递归，广度优先）
    /// </summary>
    public IReadOnlyList<string> GetDescendantAlarms(string alarmId)
    {
        var descendants = new List<string>();
        var queue = new Queue<string>();

        if (_childrenMap.TryGetValue(alarmId, out var directChildren))
        {
            lock (directChildren)
            {
                foreach (var childId in directChildren)
                    queue.Enqueue(childId);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            descendants.Add(current);

            if (_childrenMap.TryGetValue(current, out var children))
            {
                lock (children)
                {
                    foreach (var childId in children)
                        queue.Enqueue(childId);
                }
            }
        }

        return descendants.AsReadOnly();
    }

    /// <summary>
    /// 获取报警在根因树中的深度
    /// </summary>
    public int GetRootCauseDepth(string alarmId)
    {
        return _depthMap.TryGetValue(alarmId, out var depth) ? depth : 0;
    }

    /// <summary>
    /// 递归恢复报警树 — 从指定节点开始恢复整个子树
    /// </summary>
    /// <returns>恢复的报警 ID 列表（含自身和所有子节点）</returns>
    public IReadOnlyList<string> RecoverTree(string rootAlarmId)
    {
        var recovered = new List<string>();

        // BFS 收集所有子节点
        var allNodes = new List<string> { rootAlarmId };
        allNodes.AddRange(GetDescendantAlarms(rootAlarmId));

        // 移除索引（从叶子到根）
        for (int i = allNodes.Count - 1; i >= 0; i--)
        {
            var nodeId = allNodes[i];
            recovered.Add(nodeId);

            _parentMap.TryRemove(nodeId, out _);
            _childrenMap.TryRemove(nodeId, out _);
            _depthMap.TryRemove(nodeId, out _);
            _alarmGroups.TryRemove(nodeId, out _);
        }

        // 清理旧的 flat 分组索引
        var nodeSet = new HashSet<string>(recovered);
        var rootKeysToRemove = _rootCauses
            .Where(kvp => nodeSet.Contains(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in rootKeysToRemove)
            _rootCauses.TryRemove(key, out _);

        foreach (var nodeId in recovered)
        {
            _groupMembers.TryRemove(nodeId, out _);
        }

        return recovered.AsReadOnly();
    }

    public void Clear()
    {
        _rootCauses.Clear();
        _groupMembers.Clear();
        _alarmGroups.Clear();
        _parentMap.Clear();
        _childrenMap.Clear();
        _depthMap.Clear();
    }

    /// <summary>
    /// 计算报警在树中的深度 — 向上遍历到根
    /// </summary>
    private int GetRootCauseDepthInternal(string alarmId)
    {
        var depth = 0;
        var current = alarmId;

        while (_parentMap.TryGetValue(current, out var parent) && parent != null)
        {
            depth++;
            current = parent;
        }

        return depth;
    }
}
