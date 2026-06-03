using SqlSugar;
using Wcs.Core.EventBus.Events;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

/// <summary>
/// 验证器上下文 — 验证器通过它访问系统状态和数据库
///
/// 验证器可获取：
/// - StateCenter：设备/任务/报警状态
/// - Db：SqlSugarClient，直接查业务数据库
/// - Definition：当前触发的信号定义
/// - RawDiff：原始 PLC 差异数据
/// - GeneratedEvents：本次已生成的事件
///
/// 数据库查询示例：
///   var pallet = ctx.Db.Queryable<PalletTable>()
///     .Where(p => p.Barcode == barcode).First();
///   var task = ctx.Db.Queryable<TaskTable>()
///     .Where(t => t.DeviceId == deviceId && t.Status == "Running").First();
/// </summary>
public class ValidatorContext
{
    /// <summary>StateCenter — 查设备状态、任务状态、报警状态</summary>
    public IStateCenter StateCenter { get; }

    /// <summary>SqlSugarClient — 查业务数据库</summary>
    public ISqlSugarClient? Db { get; }

    /// <summary>当前触发的信号定义</summary>
    public SignalDefinition Definition { get; }

    /// <summary>原始 PLC 差异数据</summary>
    public PlcBlockDiff RawDiff { get; }

    /// <summary>本次已生成的事件列表</summary>
    public IReadOnlyList<IEvent> GeneratedEvents { get; }

    /// <summary>验证器之间共享状态的自定义数据</summary>
    public Dictionary<string, object> SharedData { get; } = new();

    public ValidatorContext(
        IStateCenter stateCenter,
        SignalDefinition definition,
        PlcBlockDiff rawDiff,
        IReadOnlyList<IEvent> generatedEvents,
        ISqlSugarClient? db = null,
        Dictionary<string, object>? sharedData = null)
    {
        StateCenter = stateCenter;
        Db = db;
        Definition = definition;
        RawDiff = rawDiff;
        GeneratedEvents = generatedEvents;
        if (sharedData != null)
            foreach (var kvp in sharedData) SharedData[kvp.Key] = kvp.Value;
    }
}
