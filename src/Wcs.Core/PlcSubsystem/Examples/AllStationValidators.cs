namespace Wcs.Core.PlcSubsystem.Examples;

using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.StateCenter.Models;

public class Cv01_ArrivalValidator : ISignalValidator
{
    public string ValidatorId => "CV01_Arrival";
    public string? DeviceId => "CV01";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV01_PalletArrived && db1.CV01_Fault) return SignalValidationResult.Reject("CV01 故障");
        if (db1.CV01_PalletArrived && !db1.CV01_DriveReady) return SignalValidationResult.Reject("CV01 未就绪");
        if (ctx.Db?.Queryable<object>().Where("StationId='CV01' AND InMaintenance=1").Any() == true)
            return SignalValidationResult.Reject("CV01 维护中");
        return SignalValidationResult.Pass("CV01 允许");
    }
}

public class Cv02_TransferValidator : ISignalValidator
{
    public string ValidatorId => "CV02_Transfer";
    public string? DeviceId => "CV02";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV02_Fault) return SignalValidationResult.Reject("CV02 故障");
        if (db1.CV02_Busy) return SignalValidationResult.Defer("CV02 繁忙", 2000);
        if (ctx.StateCenter.GetDeviceState("CV01")?.Status == DeviceStatusEnum.Running)
            return SignalValidationResult.Defer("上游 CV01 运输中", 1500);
        return SignalValidationResult.Pass("CV02 允许");
    }
}

public class Cv03_MergeValidator : ISignalValidator
{
    public string ValidatorId => "CV03_Merge";
    public string? DeviceId => "CV03";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV03_Fault) return SignalValidationResult.Reject("CV03 故障");
        if (ctx.StateCenter.GetDeviceState("LIFT01")?.Status == DeviceStatusEnum.Running || db1.CV03_Busy)
            return SignalValidationResult.Defer("LIFT01 忙或合流占用", 3000);
        return SignalValidationResult.Pass("CV03 允许");
    }
}

public class Cv04_BufferValidator : ISignalValidator
{
    public string ValidatorId => "CV04_Buffer";
    public string? DeviceId => "CV04";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV04_Fault) return SignalValidationResult.Reject("CV04 故障");
        return SignalValidationResult.Pass("CV04 允许");
    }
}

public class Cv05_WeighValidator : ISignalValidator
{
    public string ValidatorId => "CV05_Weigh";
    public string? DeviceId => "CV05";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV05_Fault) return SignalValidationResult.Reject("CV05 故障");
        return SignalValidationResult.Pass("CV05 允许");
    }
}

public class Cv06_SortEntryValidator : ISignalValidator
{
    public string ValidatorId => "CV06_SortEntry";
    public string? DeviceId => "CV06";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV06_Fault) return SignalValidationResult.Reject("CV06 故障");
        if (ctx.StateCenter.GetDeviceState("SORTER01")?.Status != DeviceStatusEnum.Idle)
            return SignalValidationResult.Defer("分拣机忙", 3000);
        return SignalValidationResult.Pass("CV06 允许");
    }
}

public class Cv07_OutboundValidator : ISignalValidator
{
    public string ValidatorId => "CV07_Outbound";
    public string? DeviceId => "CV07";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV07_Fault) return SignalValidationResult.Reject("CV07 故障");
        return SignalValidationResult.Pass("CV07 出库允许");
    }
}

public class Cv08_LiftEntryValidator : ISignalValidator
{
    public string ValidatorId => "CV08_LiftEntry";
    public string? DeviceId => "CV08";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV08_Fault) return SignalValidationResult.Reject("CV08 故障");
        if (ctx.StateCenter.GetDeviceState("LIFT01")?.Status != DeviceStatusEnum.Idle)
            return SignalValidationResult.Defer("LIFT01 非空闲", 3000);
        return SignalValidationResult.Pass("CV08 允许进入提升机");
    }
}

public class Cv09_StorageEntryValidator : ISignalValidator
{
    public string ValidatorId => "CV09_StorageEntry";
    public string? DeviceId => "CV09";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV09_Fault) return SignalValidationResult.Reject("CV09 故障");
        return SignalValidationResult.Pass("CV09 允许");
    }
}

public class Cv10_ExitValidator : ISignalValidator
{
    public string ValidatorId => "CV10_Exit";
    public string? DeviceId => "CV10";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;
        if (db1.CV10_Fault) return SignalValidationResult.Reject("CV10 故障");
        return SignalValidationResult.Pass("CV10 出口允许");
    }
}

// ==================== 堆垛机 4 台 ====================

public class Asrs01_Validator : ISignalValidator
{
    public string ValidatorId => "ASRS01_Store";
    public string? DeviceId => "ASRS01";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC2_DB1_StackerStatus db) return null;
        if (db.ASRS01_Fault) return SignalValidationResult.Reject("ASRS01 故障");
        if (db.ASRS01_Busy) return SignalValidationResult.Defer("ASRS01 繁忙", 5000);
        if (!db.ASRS01_AutoMode) return SignalValidationResult.Reject("ASRS01 非自动模式");
        return SignalValidationResult.Pass("ASRS01 允许");
    }
}

public class Asrs02_Validator : ISignalValidator
{
    public string ValidatorId => "ASRS02_Store";
    public string? DeviceId => "ASRS02";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC2_DB1_StackerStatus db) return null;
        if (db.ASRS02_Fault) return SignalValidationResult.Reject("ASRS02 故障");
        if (db.ASRS02_Busy) return SignalValidationResult.Defer("ASRS02 繁忙", 5000);
        if (!db.ASRS02_AutoMode) return SignalValidationResult.Reject("ASRS02 非自动模式");
        return SignalValidationResult.Pass("ASRS02 允许");
    }
}

public class Asrs03_Validator : ISignalValidator
{
    public string ValidatorId => "ASRS03_Store";
    public string? DeviceId => "ASRS03";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC2_DB1_StackerStatus db) return null;
        if (db.ASRS03_Fault) return SignalValidationResult.Reject("ASRS03 故障");
        if (db.ASRS03_Busy) return SignalValidationResult.Defer("ASRS03 繁忙", 5000);
        if (!db.ASRS03_AutoMode) return SignalValidationResult.Reject("ASRS03 非自动模式");
        return SignalValidationResult.Pass("ASRS03 允许");
    }
}

public class Asrs04_Validator : ISignalValidator
{
    public string ValidatorId => "ASRS04_Store";
    public string? DeviceId => "ASRS04";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC2_DB1_StackerStatus db) return null;
        if (db.ASRS04_Fault) return SignalValidationResult.Reject("ASRS04 故障");
        if (db.ASRS04_Busy) return SignalValidationResult.Defer("ASRS04 繁忙", 5000);
        if (!db.ASRS04_AutoMode) return SignalValidationResult.Reject("ASRS04 非自动模式");
        return SignalValidationResult.Pass("ASRS04 允许");
    }
}

// ==================== 机器人 4 台 ====================

public class Robot01_Validator : ISignalValidator
{
    public string ValidatorId => "ROBOT01_Grip";
    public string? DeviceId => "ROBOT01";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC3_DB1_RobotStatus r) return null;
        if (r.ROBOT01_Fault) return SignalValidationResult.Reject("ROBOT01 故障");
        if (!r.ROBOT01_PalletPresent) return SignalValidationResult.Reject("ROBOT01 无托盘");
        if (r.ROBOT01_Busy && ctx.PreviousStruct is PLC3_DB1_RobotStatus p && p.ROBOT01_Busy)
            return SignalValidationResult.Defer("ROBOT01 执行中", 2000);
        return SignalValidationResult.Pass("ROBOT01 允许");
    }
}

public class Robot02_Validator : ISignalValidator
{
    public string ValidatorId => "ROBOT02_Grip";
    public string? DeviceId => "ROBOT02";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC3_DB1_RobotStatus r) return null;
        if (r.ROBOT02_Fault) return SignalValidationResult.Reject("ROBOT02 故障");
        if (!r.ROBOT02_PalletPresent) return SignalValidationResult.Reject("ROBOT02 无托盘");
        if (r.ROBOT02_Busy && ctx.PreviousStruct is PLC3_DB1_RobotStatus p && p.ROBOT02_Busy)
            return SignalValidationResult.Defer("ROBOT02 执行中", 2000);
        return SignalValidationResult.Pass("ROBOT02 允许");
    }
}

public class Robot03_Validator : ISignalValidator
{
    public string ValidatorId => "ROBOT03_Grip";
    public string? DeviceId => "ROBOT03";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC3_DB1_RobotStatus r) return null;
        if (r.ROBOT03_Fault) return SignalValidationResult.Reject("ROBOT03 故障");
        if (!r.ROBOT03_PalletPresent) return SignalValidationResult.Reject("ROBOT03 无托盘");
        if (r.ROBOT03_Busy && ctx.PreviousStruct is PLC3_DB1_RobotStatus p && p.ROBOT03_Busy)
            return SignalValidationResult.Defer("ROBOT03 执行中", 2000);
        return SignalValidationResult.Pass("ROBOT03 允许");
    }
}

public class Robot04_Validator : ISignalValidator
{
    public string ValidatorId => "ROBOT04_Grip";
    public string? DeviceId => "ROBOT04";
    public string? SignalId => null;
    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        if (ctx.RawStruct is not PLC3_DB1_RobotStatus r) return null;
        if (r.ROBOT04_Fault) return SignalValidationResult.Reject("ROBOT04 故障");
        if (!r.ROBOT04_PalletPresent) return SignalValidationResult.Reject("ROBOT04 无托盘");
        if (r.ROBOT04_Busy && ctx.PreviousStruct is PLC3_DB1_RobotStatus p && p.ROBOT04_Busy)
            return SignalValidationResult.Defer("ROBOT04 执行中", 2000);
        return SignalValidationResult.Pass("ROBOT04 允许");
    }
}
