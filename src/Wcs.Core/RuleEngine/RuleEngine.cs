namespace Wcs.Core.RuleEngine;

using System.Collections.Concurrent;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 规则引擎实现 — 评估业务信号，匹配规则，生成任务
///
/// 工作流程：
/// SignalEvent → 遍历注册规则 → 检查条件（AND）→ 全部满足 → 生成 TaskContext
///
/// 支持按 ContextKey 分组状态（如按 DeviceId 分别跟踪规则状态）
/// </summary>
public class RuleEngine : IRuleEngine
{
    private readonly ConcurrentDictionary<string, RuleDefinition> _rules = new();

    // 规则状态：contextKey → (ruleId → matchedConditionsCount)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _contextStates = new();

    // 信号类型索引：signalType → ruleIds
    private readonly ConcurrentDictionary<string, HashSet<string>> _signalIndex = new();

    private readonly object _lock = new();
    private long _totalEvaluations;
    private long _totalTasksGenerated;

    /// <summary>
    /// 注册规则
    /// </summary>
    public void RegisterRule(RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("Rule name is required");

        lock (_lock)
        {
            _rules[rule.RuleId] = rule;
            RebuildSignalIndex();
        }
    }

    /// <summary>
    /// 批量注册规则
    /// </summary>
    public void RegisterRules(IEnumerable<RuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        lock (_lock)
        {
            foreach (var rule in rules)
                _rules[rule.RuleId] = rule;
            RebuildSignalIndex();
        }
    }

    /// <summary>
    /// 移除规则
    /// </summary>
    public bool RemoveRule(string ruleId)
    {
        lock (_lock)
        {
            if (_rules.TryRemove(ruleId, out _))
            {
                RebuildSignalIndex();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 获取所有规则
    /// </summary>
    public IReadOnlyList<RuleDefinition> GetRules()
    {
        return _rules.Values.OrderBy(r => r.Priority).ToList();
    }

    /// <summary>
    /// 启用/禁用规则
    /// </summary>
    public void SetRuleEnabled(string ruleId, bool enabled)
    {
        if (_rules.TryGetValue(ruleId, out var rule))
            rule.Enabled = enabled;
    }

    /// <summary>
    /// 评估业务信号 — 核心方法
    /// </summary>
    public IReadOnlyList<TaskContext> Evaluate(object signalEvent)
    {
        Interlocked.Increment(ref _totalEvaluations);

        var generatedTasks = new List<TaskContext>();
        var signalType = signalEvent.GetType().Name;

        // 查找可能匹配的规则
        List<RuleDefinition> candidates;
        lock (_lock)
        {
            if (!_signalIndex.TryGetValue(signalType, out var ruleIds) || ruleIds.Count == 0)
                return generatedTasks;

            candidates = ruleIds
                .Select(id => _rules.TryGetValue(id, out var r) ? r : null)
                .Where(r => r != null && r.Enabled)
                .Cast<RuleDefinition>()
                .ToList();
        }

        foreach (var rule in candidates)
        {
            // 检查当前信号匹配规则中某个条件
            var matchedCondition = rule.Conditions.FirstOrDefault(c => c.Matches(signalEvent));
            if (matchedCondition == null)
                continue;

            // 确定上下文键
            var contextKey = ResolveContextKey(rule, signalEvent);

            // 更新条件匹配计数
            var matchedCount = IncrementConditionMatch(rule.RuleId, contextKey);

            // 检查是否所有条件都已满足
            if (matchedCount >= rule.Conditions.Count)
            {
                var task = GenerateTask(rule, signalEvent);
                if (task != null)
                {
                    generatedTasks.Add(task);
                    Interlocked.Increment(ref _totalTasksGenerated);

                    // 生成任务后重置上下文状态
                    ResetContext(contextKey);
                }
            }
        }

        return generatedTasks;
    }

    /// <summary>
    /// 重置指定上下文的规则状态
    /// </summary>
    public void ResetContext(string contextKey)
    {
        _contextStates.TryRemove(contextKey, out _);
    }

    /// <summary>
    /// 重置所有规则状态
    /// </summary>
    public void ResetAll()
    {
        _contextStates.Clear();
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    public RuleEngineStats GetStats()
    {
        return new RuleEngineStats
        {
            RuleCount = _rules.Count,
            TotalEvaluations = Interlocked.Read(ref _totalEvaluations),
            TotalTasksGenerated = Interlocked.Read(ref _totalTasksGenerated),
            ActiveContexts = _contextStates.Count
        };
    }

    /// <summary>
    /// 重建信号类型 → 规则 ID 索引
    /// </summary>
    private void RebuildSignalIndex()
    {
        _signalIndex.Clear();
        foreach (var rule in _rules.Values)
        {
            foreach (var condition in rule.Conditions)
            {
                var signalType = condition.SignalType;
                var ids = _signalIndex.GetOrAdd(signalType, _ => new HashSet<string>());
                ids.Add(rule.RuleId);
            }
        }
    }

    /// <summary>
    /// 解析上下文键
    /// </summary>
    private static string ResolveContextKey(RuleDefinition rule, object signalEvent)
    {
        if (string.IsNullOrEmpty(rule.ContextKey))
            return "_global";

        // 从信号事件属性中提取上下文键值
        var prop = signalEvent.GetType().GetProperty(rule.ContextKey);
        if (prop != null)
        {
            var value = prop.GetValue(signalEvent)?.ToString();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return "_global";
    }

    /// <summary>
    /// 递增条件匹配计数
    /// </summary>
    private int IncrementConditionMatch(string ruleId, string contextKey)
    {
        var context = _contextStates.GetOrAdd(contextKey, _ => new ConcurrentDictionary<string, int>());
        return context.AddOrUpdate(ruleId, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// 从规则和信号生成任务
    /// </summary>
    private static TaskContext? GenerateTask(RuleDefinition rule, object signalEvent)
    {
        if (rule.Action == null || !rule.Action.Enabled)
            return null;

        var signalType = signalEvent.GetType();
        var task = new TaskContext
        {
            Priority = rule.Action.Priority,
            Tags = { ["RuleId"] = rule.RuleId, ["RuleName"] = rule.Name }
        };

        // 解析 DeviceId — 支持 @PropertyName 引用
        if (!string.IsNullOrEmpty(rule.Action.DeviceId))
        {
            task.DeviceId = ResolveProperty(rule.Action.DeviceId, signalEvent, signalType);
        }

        // 解析任务参数
        foreach (var param in rule.Action.ParameterTemplates)
        {
            var value = ResolveProperty(param.Value, signalEvent, signalType);
            task.Parameters[param.Key] = value;
        }

        return task;
    }

    /// <summary>
    /// 解析属性引用（@PropertyName → 信号事件的属性值）
    /// </summary>
    private static string ResolveProperty(string template, object signalEvent, Type signalType)
    {
        if (template.StartsWith("@"))
        {
            var propName = template.TrimStart('@');
            var prop = signalType.GetProperty(propName);
            if (prop != null)
            {
                return prop.GetValue(signalEvent)?.ToString() ?? template;
            }
        }
        return template;
    }
}
