using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

public class ValidationRuleConfig
{
    public string RuleId { get; set; } = string.Empty;
    public string? TargetDeviceId { get; set; }
    public string? TargetSignalId { get; set; }
    public ConditionGroup Conditions { get; set; } = new();
    public string? OnRejectMessage { get; set; }
    public bool Enabled { get; set; } = true;
}

public class ConditionGroup
{
    public string Operator { get; set; } = "AND";
    public List<ConditionItem> Items { get; set; } = new();
    public List<ConditionGroup>? Groups { get; set; }
}

public class ConditionItem
{
    public string CheckType { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? ExpectedStatus { get; set; }
    public string? ExpectedValue { get; set; }
    public string Comparator { get; set; } = "Equals";
}

/// <summary>
/// 配置化信号验证器 — 根据 JSON 规则验证信号，无需写代码
/// 支持多层 AND/OR 嵌套条件
/// </summary>
public class ConfigurableSignalValidator : ISignalValidator
{
    public string ValidatorId => "ConfigurableRuleEngine";
    public string? DeviceId => null;
    public string? SignalId => null;

    private readonly List<ValidationRuleConfig> _rules;
    private readonly IStateCenter _stateCenter;
    private readonly ILogger<ConfigurableSignalValidator> _logger;

    public ConfigurableSignalValidator(
        List<ValidationRuleConfig> rules,
        IStateCenter stateCenter,
        ILogger<ConfigurableSignalValidator> logger)
    {
        _rules = rules;
        _stateCenter = stateCenter;
        _logger = logger;
    }

    public SignalValidationResult? Validate(
        SignalDefinition definition,
        PlcBlockDiff diff,
        IReadOnlyList<IEvent> generatedEvents)
    {
        var signalId = definition.SignalId;
        var deviceId = definition.PropertyMappings.GetValueOrDefault("DeviceId") ?? "";

        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;
            if (rule.TargetDeviceId != null && rule.TargetDeviceId != deviceId) continue;
            if (rule.TargetSignalId != null && rule.TargetSignalId != signalId) continue;

            var match = EvalGroup(rule.Conditions);
            if (!match.Item1)
            {
                var msg = rule.OnRejectMessage ?? $"验证规则 {rule.RuleId} 拒绝";
                _logger.LogWarning("[验证] ❌ {RuleId}: {Device}/{Signal} {Msg} (原因: {Reason})",
                    rule.RuleId, deviceId, signalId, msg, match.Item2);
                return SignalValidationResult.Reject(msg);
            }
        }
        return null;
    }

    private (bool, string?) EvalGroup(ConditionGroup g)
    {
        var results = new List<(bool, string?)>();
        foreach (var item in g.Items) results.Add(EvalItem(item));
        if (g.Groups != null)
            foreach (var sub in g.Groups) results.Add(EvalGroup(sub));
        if (results.Count == 0) return (true, null);

        var isAnd = g.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase);
        if (isAnd)
        {
            var f = results.FirstOrDefault(r => !r.Item1);
            return f.Item1 ? (true, null) : (false, f.Item2);
        }
        var p = results.FirstOrDefault(r => r.Item1);
        return p.Item1 ? (true, null) : (false, "OR 条件全部不满足");
    }

    private (bool, string?) EvalItem(ConditionItem item)
    {
        if (item.CheckType == "AlwaysPass") return (true, null);
        if (item.CheckType == "AlwaysReject") return (false, item.ExpectedValue ?? "AlwaysReject");
        if (item.CheckType == "DeviceState")
        {
            if (string.IsNullOrEmpty(item.DeviceId)) return (true, null);
            var state = _stateCenter.GetDeviceState(item.DeviceId);
            var cur = state?.Status.ToString() ?? "Offline";
            var match = cur.Equals(item.ExpectedStatus, StringComparison.OrdinalIgnoreCase);
            return match ? (true, null) : (false, $"{item.DeviceId} 状态={cur}≠{item.ExpectedStatus}");
        }
        return (true, null);
    }
}
