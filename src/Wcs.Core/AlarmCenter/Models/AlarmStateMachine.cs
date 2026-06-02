namespace Wcs.Core.AlarmCenter.Models;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 5 状态报警状态机 — 管理报警状态间的合法转换
///
/// 合法转换:
///   Normal ──raise──→ PendingRaise ──confirmed──→ Active ──ack──→ Acknowledged
///     ↑                    │                          │                  │
///     └──canceled──────────┘    ←──rebounce─────────┘                  │
///                                                                      │
///   Recovered ←──confirmed── PendingRecover ←──recover───┘
///        ↑                        │
///        └───────rebounce─────────┘
/// </summary>
public static class AlarmStateMachine
{
    /// <summary>
    /// 验证状态转换是否合法
    /// </summary>
    public static bool IsValidTransition(AlarmStatusEnum from, AlarmStatusEnum to)
    {
        return (from, to) switch
        {
            (AlarmStatusEnum.Normal, AlarmStatusEnum.PendingRaise) => true,
            (AlarmStatusEnum.PendingRaise, AlarmStatusEnum.Active) => true,
            (AlarmStatusEnum.PendingRaise, AlarmStatusEnum.Normal) => true,
            (AlarmStatusEnum.Active, AlarmStatusEnum.Acknowledged) => true,
            (AlarmStatusEnum.Active, AlarmStatusEnum.PendingRecover) => true,
            (AlarmStatusEnum.Acknowledged, AlarmStatusEnum.PendingRecover) => true,
            (AlarmStatusEnum.PendingRecover, AlarmStatusEnum.Recovered) => true,
            (AlarmStatusEnum.PendingRecover, AlarmStatusEnum.Active) => true,
            _ => false
        };
    }

    /// <summary>
    /// 执行状态转换，非法转换抛出 InvalidOperationException
    /// </summary>
    public static void Transition(AlarmState state, AlarmStatusEnum to)
    {
        if (!IsValidTransition(state.Status, to))
        {
            throw new InvalidOperationException(
                $"Invalid alarm state transition: {state.Status} → {to} for alarm {state.AlarmId}");
        }
        state.Status = to;
    }

    /// <summary>
    /// 获取该状态下可用的目标状态列表
    /// </summary>
    public static IReadOnlyList<AlarmStatusEnum> GetAvailableTargets(AlarmStatusEnum current)
    {
        return current switch
        {
            AlarmStatusEnum.Normal => new[] { AlarmStatusEnum.PendingRaise },
            AlarmStatusEnum.PendingRaise => new[] { AlarmStatusEnum.Active, AlarmStatusEnum.Normal },
            AlarmStatusEnum.Active => new[] { AlarmStatusEnum.Acknowledged, AlarmStatusEnum.PendingRecover },
            AlarmStatusEnum.Acknowledged => new[] { AlarmStatusEnum.PendingRecover },
            AlarmStatusEnum.PendingRecover => new[] { AlarmStatusEnum.Recovered, AlarmStatusEnum.Active },
            AlarmStatusEnum.Recovered => Array.Empty<AlarmStatusEnum>(),
            _ => Array.Empty<AlarmStatusEnum>()
        };
    }

    /// <summary>
    /// 该报警是否可操作（非终态）
    /// </summary>
    public static bool IsActive(AlarmStatusEnum status) => status
        is AlarmStatusEnum.PendingRaise
        or AlarmStatusEnum.Active
        or AlarmStatusEnum.Acknowledged
        or AlarmStatusEnum.PendingRecover;

    /// <summary>
    /// 是否需要显示在报警面板上
    /// </summary>
    public static bool IsVisible(AlarmStatusEnum status) => status
        is AlarmStatusEnum.Active
        or AlarmStatusEnum.Acknowledged
        or AlarmStatusEnum.PendingRecover;
}
