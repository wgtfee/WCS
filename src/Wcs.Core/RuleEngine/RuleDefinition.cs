namespace Wcs.Core.RuleEngine;

/// <summary>
/// 纯 WCS 规则引擎 — 只允许「信号 → 运输任务」映射
///
/// ✅ WCS 允许：
///   - PLC 信号（托盘到位/输送线就绪）→ 生成 TransportTask
///   - 设备故障信号 → 生成 RecoveryTask
///   - 条件满足 → 生成 MoveTask
///
/// ❌ WMS 禁止：
///   - 订单规则（OrderRule）
///   - 库存规则（InventoryRule）
///   - 批次规则（BatchRule）
///   - 波次规则（WaveRule）
///   - 库位分配（LocationAllocation）
///   - 入库策略（PutawayStrategy）
///   - 出库策略（PickingStrategy）
/// </summary>
public class RuleCondition
{
    /// <summary>要匹配的业务信号事件全类型名</summary>
    public string SignalType { get; set; } = string.Empty;

    /// <summary>属性匹配器：属性名 → 期望值</summary>
    public Dictionary<string, string> PropertyMatchers { get; set; } = new();

    /// <summary>检查条件是否匹配指定的信号</summary>
    public bool Matches(object signalEvent)
    {
        if (signalEvent == null) return false;
        var signalType = signalEvent.GetType();

        if (signalType.FullName != SignalType && signalType.Name != SignalType)
            return false;

        foreach (var matcher in PropertyMatchers)
        {
            var prop = signalType.GetProperty(matcher.Key);
            if (prop == null) return false;

            var value = prop.GetValue(signalEvent)?.ToString();
            if (value != matcher.Value) return false;
        }

        return true;
    }
}

/// <summary>
/// 规则动作 — 条件满足时的操作
/// </summary>
public class RuleAction
{
    /// <summary>动作类型</summary>
    public string ActionType { get; set; } = "CreateTask";

    /// <summary>目标任务类型（如 "MoveTask", "StoreTask"）</summary>
    public string TaskType { get; set; } = "MoveTask";

    /// <summary>任务优先级</summary>
    public int Priority { get; set; } = 2;

    /// <summary>目标设备 ID（可用信号属性引用如 @DeviceId）</summary>
    public string? DeviceId { get; set; }

    /// <summary>任务参数模板（值可用 @PropertyName 引用信号属性）</summary>
    public Dictionary<string, string> ParameterTemplates { get; set; } = new();

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 业务规则定义 — 信号条件 → 任务生成
/// </summary>
public class RuleDefinition
{
    /// <summary>规则唯一标识</summary>
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>规则名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>规则描述</summary>
    public string? Description { get; set; }

    /// <summary>规则条件列表（AND 逻辑：全部满足才触发）</summary>
    public List<RuleCondition> Conditions { get; set; } = new();

    /// <summary>规则动作</summary>
    public RuleAction Action { get; set; } = new();

    /// <summary>规则优先级（越小越优先）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上下文分组键（如 DeviceId）— 同一组内共享状态</summary>
    public string? ContextKey { get; set; }
}
