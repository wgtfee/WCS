using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Core.PlcSubsystem.Validation.Examples;

/// <summary>
/// 标签版工位互锁验证器 — 从 ctx.RawStruct 强类型读取标签类属性
///
/// 与 StationInterlockValidator 完全相同的逻辑，
/// 但操作的是 [PlcTag] 特性修饰的类（而非 struct 字段）
///
/// 注册方式：
///   eventDetector.RegisterValidator(new TagStationInterlockValidator());
/// </summary>
public class TagStationInterlockValidator : ISignalValidator
{
    public string ValidatorId => "TagStationInterlock";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        var status = ctx.RawStruct as TagConveyorStatus;
        if (status == null || !status.PalletArrived) return null;

        // 互锁判断：下游就绪时拒绝新托盘
        if (status.DriveReady)
            return SignalValidationResult.Reject("下游驱动就绪（忙），拒绝新托盘");

        // 结合 StateCenter 读取其他设备状态
        var liftState = ctx.StateCenter.GetDeviceState("LIFT01");
        if (liftState == null || liftState.Status == StateCenter.Models.DeviceStatusEnum.Idle)
        {
            // 验证通过 + 携带要执行的命令 → EventDetector 自动发 CommandRequestedEvent
            return SignalValidationResult.Pass("上下游就绪，允许到位")
                .WithCommand(new TagControlCommand { StartStation1 = true, SpeedSetpoint1 = 500 },
                    "StartConveyor", deviceId: "CV01");
        }

        return SignalValidationResult.Defer("LIFT01 不为空闲，延迟处理", retryAfterMs: 3000);
    }
}

/// <summary>
/// 标签版条码验证器 — 结合数据库验证
/// </summary>
public class TagBarcodeDbValidator : ISignalValidator
{
    public string ValidatorId => "TagBarcodeDbCheck";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        var status = ctx.RawStruct as TagConveyorStatus;
        if (status == null || !status.PalletArrived || ctx.Db == null)
            return null;

        var barcode = $"PALLET_{status.Speed:D6}";

        var exists = ctx.Db.Queryable<object>()
            .Where("Barcode = @b", new { b = barcode }).Any();
        if (!exists)
            return SignalValidationResult.Reject($"条码 {barcode} 未注册");

        return SignalValidationResult.Pass($"条码 {barcode} 验证通过");
    }
}
