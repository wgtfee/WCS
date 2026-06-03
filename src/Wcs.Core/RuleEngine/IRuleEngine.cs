namespace Wcs.Core.RuleEngine;

using Wcs.Core.TaskEngine.Context;

/// <summary>
/// 规则引擎接口 — 信号 → 规则匹配 → 任务生成
/// 实现 PLC 信号与业务逻辑的完全解耦
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// 注册规则
    /// </summary>
    void RegisterRule(RuleDefinition rule);

    /// <summary>
    /// 批量注册规则
    /// </summary>
    void RegisterRules(IEnumerable<RuleDefinition> rules);

    /// <summary>
    /// 移除规则
    /// </summary>
    bool RemoveRule(string ruleId);

    /// <summary>
    /// 获取所有规则
    /// </summary>
    IReadOnlyList<RuleDefinition> GetRules();

    /// <summary>
    /// 启用/禁用规则
    /// </summary>
    void SetRuleEnabled(string ruleId, bool enabled);

    /// <summary>
    /// 评估业务信号 — 匹配规则，满足条件时生成任务
    /// </summary>
    /// <returns>生成的任务列表</returns>
    IReadOnlyList<TaskContext> Evaluate(object signalEvent);

    /// <summary>
    /// 重置指定上下文的规则状态（如设备完成一轮作业后重置）
    /// </summary>
    void ResetContext(string contextKey);

    /// <summary>
    /// 重置所有规则状态
    /// </summary>
    void ResetAll();

    /// <summary>
    /// 规则引擎统计
    /// </summary>
    RuleEngineStats GetStats();
}

/// <summary>
/// 规则引擎统计
/// </summary>
public class RuleEngineStats
{
    public int RuleCount { get; set; }
    public long TotalEvaluations { get; set; }
    public long TotalTasksGenerated { get; set; }
    public int ActiveContexts { get; set; }
}
