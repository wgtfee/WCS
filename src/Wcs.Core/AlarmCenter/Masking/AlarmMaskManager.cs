namespace Wcs.Core.AlarmCenter.Masking;

using System.Collections.Concurrent;

/// <summary>
/// 报警屏蔽管理器 — 管理报警屏蔽规则，用于设备维修等场景
/// </summary>
public class AlarmMaskManager
{
    private readonly ConcurrentDictionary<string, AlarmMaskRule> _rules = new();
    private readonly object _lock = new();

    /// <summary>
    /// 添加屏蔽规则
    /// </summary>
    public void AddRule(AlarmMaskRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules[rule.MaskId] = rule;
    }

    /// <summary>
    /// 移除屏蔽规则
    /// </summary>
    public bool RemoveRule(string maskId)
    {
        return _rules.TryRemove(maskId, out _);
    }

    /// <summary>
    /// 获取所有屏蔽规则
    /// </summary>
    public IReadOnlyList<AlarmMaskRule> GetRules()
    {
        return _rules.Values.ToList();
    }

    /// <summary>
    /// 获取当前生效的屏蔽规则
    /// </summary>
    public IReadOnlyList<AlarmMaskRule> GetActiveRules()
    {
        return _rules.Values.Where(r => r.IsActive()).ToList();
    }

    /// <summary>
    /// 检查指定设备的指定报警是否被屏蔽
    /// 优先级：DeviceId+AlarmCode 精确匹配 > DeviceId 全局 > AlarmCode 全局 > 全局规则
    /// </summary>
    public bool IsMasked(string? deviceId, string? alarmCode)
    {
        foreach (var rule in _rules.Values)
        {
            if (!rule.IsActive())
                continue;

            // 精确匹配：DeviceId + AlarmCode
            if (rule.DeviceId != null && rule.AlarmCode != null)
            {
                if (rule.DeviceId == deviceId && rule.AlarmCode == alarmCode)
                    return true;
                continue;
            }

            // 设备级屏蔽：DeviceId 匹配，无 AlarmCode
            if (rule.DeviceId != null && rule.AlarmCode == null)
            {
                if (rule.DeviceId == deviceId)
                    return true;
                continue;
            }

            // 报警码级屏蔽：AlarmCode 匹配，无 DeviceId
            if (rule.DeviceId == null && rule.AlarmCode != null)
            {
                if (rule.AlarmCode == alarmCode)
                    return true;
                continue;
            }

            // 全局屏蔽：无 DeviceId 无 AlarmCode
            if (rule.DeviceId == null && rule.AlarmCode == null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 清除所有规则
    /// </summary>
    public void Clear()
    {
        _rules.Clear();
    }

    /// <summary>
    /// 获取规则数量
    /// </summary>
    public int Count => _rules.Count;

    /// <summary>
    /// 清理已过期的规则
    /// </summary>
    public int CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var expired = _rules.Values
            .Where(r => r.EndTime.HasValue && r.EndTime < now)
            .ToList();

        foreach (var rule in expired)
            _rules.TryRemove(rule.MaskId, out _);

        return expired.Count;
    }
}
