using SqlSugar;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Core.PlcSubsystem.Validation;

/// <summary>
/// 验证器上下文 — 验证器通过它访问系统状态、数据库和 PLC 结构体
///
/// 验证器可获取：
/// - StateCenter：设备/任务/报警状态
/// - Db：SqlSugarClient，直接查业务数据库
/// - RawStruct：本次读取的完整 PLC DB 块结构体（强类型）
/// - PreviousStruct：上一次读取的结构体（用于对比新旧值）
///
/// 使用示例：
///   var db1 = ctx.RawStruct as DB1_Struct;
///   if (db1?.CV01_Ready == true) { ... }
///   ctx.Db.Queryable<Pallet>().Where(p => p.Barcode == barcode).First();
/// </summary>
public class ValidatorContext
{
    /// <summary>StateCenter — 查设备状态、任务状态、报警状态</summary>
    public IStateCenter StateCenter { get; }

    /// <summary>SqlSugarClient — 查业务数据库</summary>
    public ISqlSugarClient? Db { get; }

    /// <summary>本次读取的完整 PLC DB 块结构体，验证器转型为具体类型后直接访问字段</summary>
    public object? RawStruct { get; }

    /// <summary>上一次读取的 PLC DB 块结构体（用于对比新旧值）</summary>
    public object? PreviousStruct { get; }

    /// <summary>验证器之间共享状态的自定义数据</summary>
    public Dictionary<string, object> SharedData { get; } = new();

    public ValidatorContext(
        IStateCenter stateCenter,
        object? rawStruct = null,
        object? previousStruct = null,
        ISqlSugarClient? db = null,
        Dictionary<string, object>? sharedData = null)
    {
        StateCenter = stateCenter;
        RawStruct = rawStruct;
        PreviousStruct = previousStruct;
        Db = db;
        if (sharedData != null)
            foreach (var kvp in sharedData) SharedData[kvp.Key] = kvp.Value;
    }
}
