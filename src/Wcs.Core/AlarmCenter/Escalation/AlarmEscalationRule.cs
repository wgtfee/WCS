namespace Wcs.Core.AlarmCenter.Escalation;

using Wcs.Core.AlarmCenter.Models;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 报警升级规则 — 报警持续未处理时逐级上报
///
/// 工业现场典型场景：
/// 1 级（1 分钟）：通知班长
/// 2 级（5 分钟）：通知主管
/// 3 级（10 分钟）：停线
/// </summary>
public class AlarmEscalationRule
{
    /// <summary>规则 ID</summary>
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>目标报警代码（null=所有报警）</summary>
    public string? AlarmCode { get; set; }

    /// <summary>目标设备 ID（null=所有设备）</summary>
    public string? DeviceId { get; set; }

    /// <summary>最低级别（只有 >= 此级别的报警触发升级）</summary>
    public AlarmLevelEnum MinLevel { get; set; } = AlarmLevelEnum.Error;

    /// <summary>升级级别列表 — 按顺序触发</summary>
    public List<EscalationLevel> Levels { get; set; } = new();

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>规则描述</summary>
    public string? Description { get; set; }
}

/// <summary>
/// 升级级别
/// </summary>
public class EscalationLevel
{
    /// <summary>级别序号（1,2,3...）</summary>
    public int Level { get; set; }

    /// <summary>延迟（从报警产生开始计算）</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>通知目标</summary>
    public string NotifyTarget { get; set; } = string.Empty;

    /// <summary>动作类型：Notify / StopLine / CallMaintenance</summary>
    public string ActionType { get; set; } = "Notify";

    /// <summary>动作参数</summary>
    public string? ActionParam { get; set; }
}
