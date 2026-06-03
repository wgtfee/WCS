namespace Wcs.Core.PlcSubsystem.Validation.Examples;

/// <summary>
/// 工位互锁验证器 — 从 ctx.RawStruct 强类型读取 PLC 信号，
/// 结合 StateCenter 中的设备状态，判断是否允许信号通过。
///
/// 验证器通过 ctx.RawStruct 拿到完整的 PLC DB 块结构体，
/// 转型后直接读字段（强类型，IDE 智能提示）：
///   var db1 = ctx.RawStruct as DB1_Struct;
///   if (db1?.CV01_PalletArrived == true) { ... }
/// </summary>
public class StationInterlockValidator : ISignalValidator
{
    public string ValidatorId => "StationInterlock";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        var db1 = ctx.RawStruct as DB1_Struct;
        if (db1 == null || !db1.CV01_PalletArrived) return null;

        // 从同一个 struct 读上下游设备状态做互锁判断
        if (db1.CV02_DriveReady)
            return SignalValidationResult.Reject("上游 CV02 驱动就绪（忙），拒绝新托盘");

        if (!db1.LIFT01_Idle)
            return SignalValidationResult.Defer("LIFT01 不为空闲，延迟处理", retryAfterMs: 3000);

        return SignalValidationResult.Pass("上下游就绪，允许到位");
    }
}

/// <summary>
/// 条码验证器 — struct + 数据库
/// 从 ctx.RawStruct 读条码，ctx.Db 查数据库确认有效性
/// </summary>
public class BarcodeDbValidator : ISignalValidator
{
    public string ValidatorId => "BarcodeDbCheck";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        var db1 = ctx.RawStruct as DB1_Struct;
        if (db1 == null || !db1.CV01_PalletArrived || ctx.Db == null)
            return null;

        var barcode = $"PALLET_{db1.CV01_Speed:D6}";

        var exists = ctx.Db.Queryable<object>()
            .Where("Barcode = @b", new { b = barcode }).Any();
        if (!exists)
            return SignalValidationResult.Reject($"条码 {barcode} 未注册");

        return SignalValidationResult.Pass($"条码 {barcode} 验证通过");
    }
}

/// <summary>
/// PLC DB1 块结构体（示例 — 字段顺序与 PLC DB 块字节顺序一致）
/// Struct.FromBytes<DB1_Struct>(bytes) 自动按字段顺序填充
/// </summary>
public class DB1_Struct
{
    public bool CV01_DriveReady { get; set; }       // DB1.DBX0.0
    public bool CV01_PalletArrived { get; set; }    // DB1.DBX0.1
    public bool CV02_DriveReady { get; set; }       // DB1.DBX0.2
    public bool LIFT01_Idle { get; set; }           // DB1.DBX0.3
    public bool ASRS01_IsBusy { get; set; }         // DB1.DBX0.4
    public short CV01_Speed { get; set; }           // DB1.DBW2
}
