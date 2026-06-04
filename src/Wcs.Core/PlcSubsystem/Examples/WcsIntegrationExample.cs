// ====================================================================
// 3 PLC 完整读写链路 + 7 个工位验证器
// ====================================================================
// PLC1 (192.168.0.1) — 输送线 DB1(读) + 提升机 DB2(读)
//   ├─ DB101 → ConveyorCommand  [PlcBlock("PLC1",101)]
//   └─ DB102 → LiftCommand      [PlcBlock("PLC1",102)]
// PLC2 (192.168.0.2) — 堆垛机
//   └─ DB201 → AsrsStoreCommand [PlcBlock("PLC2",201)]
// PLC3 (192.168.0.3) — 机器人+分拣线 DB1(读)
//   ├─ DB101 → RobotCommand     [PlcBlock("PLC3",101)]
//   └─ DB102 → SorterCommand    [PlcBlock("PLC3",102)]

// ====================================================================
// 读链路：S7PollingService 自动轮询所有 PlcBlocks
// 写链路：CommandCenter.SendStructuredCommandAsync() → [PlcBlock] → WritePool
// 验证链路：S7PollingService.RegisterValidator(ISignalValidator)
// ====================================================================


// ====================================================================
// 一、读链路 — 3 个 PLC 独立轮询（S7PollingService 自动）
// ====================================================================
// S7PollingService 为每个 PlcBlock 启动独立 Timer，循环执行：
//
//   ReadPool.Get("PLC1").ReadAsync(DB1, 0, 6)
//     → byte[6]
//     → Struct.FromBytes(typeof(DB1_StatusBlock), data, 6, 0)
//     → DB1_StatusBlock { CV01_PalletArrived, CV01_Fault, LIFT01_Idle, ... }
//     → 逐字段对比 previous
//     → StateCenter.UpdateDeviceState("CV01", { Status = Running })
//     → EventBus.PublishAsync(DeviceStateChangedEvent { DeviceId = "CV01" })
//
//   ReadPool.Get("PLC3").ReadAsync(DB1, 0, 6)
//     → byte[6]
//     → Struct.FromBytes(typeof(PLC3_RobotBlock), data, 6, 0)
//     → PLC3_RobotBlock { ROBOT01_Busy, SORTER01_Fault, ... }
//     → StateCenter.Update("ROBOT01", ...)
//


// ====================================================================
// 二、写链路 — 每个命令通过 [PlcBlock] 自描述目标位置
// ====================================================================
// CommandCenter.SendStructuredCommandAsync 内部自动：
//   1. 读 struct 上的 [PlcBlock("PLC1",101)]
//   2. PlcWriter.WriteStructAsync()
//   3. PlcSerializer 按 [PlcOffset] 序列化为 byte[]
//   4. WritePool.Get("PLC1").WriteAsync(DB101, 0, byte[])
//
// 输送机控制 → PLC1.DB101
//   await cmdCenter.SendStructuredCommandAsync("CV01", "Start",
//       new ConveyorCommand { Start = true, Speed = 1500 });
//   // DB101.DBX0.0 = 1, DB101.DBW2 = 1500
//
// 提升机控制 → PLC1.DB102
//   await cmdCenter.SendStructuredCommandAsync("LIFT01", "LiftUp",
//       new LiftCommand { GoUp = true, TargetFloor = 3 });
//   // DB102.DBX0.0 = 1, DB102.DBW2 = 3
//
// 堆垛机入库 → PLC2.DB201
//   await cmdCenter.SendStructuredCommandAsync("ASRS01", "Store",
//       new AsrsStoreCommand { StartStore = true, Column = 15, Row = 8 });
//   // DB201.DBX0.0 = 1, DB201.DBW2 = 15, DB201.DBW4 = 8
//
// 机器人抓取 → PLC3.DB101
//   await cmdCenter.SendStructuredCommandAsync("ROBOT01", "Grip",
//       new RobotCommand { Grip = true, TargetPos = 3, Speed = 800 });
//   // DB101.DBX0.0 = 1, DB101.DBW2 = 3, DB101.DBW4 = 800
//
// 分拣线启动 → PLC3.DB102
//   await cmdCenter.SendStructuredCommandAsync("SORTER01", "Start",
//       new SorterCommand { Start = true, SortTarget = 7 });
//   // DB102.DBX0.0 = 1, DB102.DBW2 = 7


// ====================================================================
// 三、工位验证器 — 每个工位一个 ISignalValidator 实现
// ====================================================================
// 注册到 S7PollingService.RegisterValidator()，每次 PLC 读到数据后自动验证
// 验证器通过 ValidatorContext 获取：
//   ctx.RawStruct       — 当前 PLC 数据块（强类型，转型后直接读字段）
//   ctx.PreviousStruct  — 上一次读的数据块（用于方向/状态变化检测）
//   ctx.StateCenter     — 设备/任务/报警状态
//   ctx.Db              — ISqlSugarClient 数据库查询
// ====================================================================


// (1) CV01 入口 — 故障检查 + 数据库条码验证
// public class Cv01_ArrivalValidator : ISignalValidator
// {
//     public string ValidatorId => "CV01_Arrival";
//     public string? DeviceId => "CV01";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var db1 = ctx.RawStruct as DB1_StatusBlock;
//         if (db1 == null || !db1.CV01_PalletArrived) return null;
//         if (db1.CV01_Fault) return SignalValidationResult.Reject("CV01 故障");
//         if (ctx.Db != null)
//         {
//             var barcode = $"PALLET_{db1.CV01_Speed:D6}";
//             var ok = ctx.Db.Queryable<object>()
//                 .Where("Barcode = @b AND Status = 'Pending'", new { b = barcode }).Any();
//             if (!ok) return SignalValidationResult.Reject($"条码 {barcode} 无待处理任务");
//         }
//         return SignalValidationResult.Pass("允许到位");
//     }
// }

// (2) CV02 转运 — 上游 CV01 运输中则延迟
// public class Cv02_TransferValidator : ISignalValidator
// {
//     public string ValidatorId => "CV02_Transfer";
//     public string? DeviceId => "CV02";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var db1 = ctx.RawStruct as DB1_StatusBlock;
//         if (db1 == null) return null;
//         if (db1.CV02_Fault) return SignalValidationResult.Reject("CV02 故障");
//         var up = ctx.StateCenter.GetDeviceState("CV01");
//         if (up?.Status == DeviceStatusEnum.Running)
//             return SignalValidationResult.Defer("CV01 运输中", 2000);
//         return SignalValidationResult.Pass("允许运输");
//     }
// }

// (3) CV03 合流 — 三线汇聚点，LIFT01 空闲才放行
// public class Cv03_MergeValidator : ISignalValidator
// {
//     public string ValidatorId => "CV03_Merge";
//     public string? DeviceId => "CV03";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var db1 = ctx.RawStruct as DB1_StatusBlock;
//         if (db1 == null) return null;
//         if (db1.CV03_Fault) return SignalValidationResult.Reject("CV03 故障");
//         if (!db1.LIFT01_Idle) return SignalValidationResult.Defer("LIFT01 忙", 3000);
//         var lift = ctx.StateCenter.GetDeviceState("LIFT01");
//         if (lift?.Status == DeviceStatusEnum.Error) return SignalValidationResult.Reject("LIFT01 故障");
//         return SignalValidationResult.Pass("合流允许");
//     }
// }

// (4) LIFT01 提升机 — 查状态变化方向 + 数据库维护状态
// public class Lift01_Validator : ISignalValidator
// {
//     public string ValidatorId => "LIFT01_Check";
//     public string? DeviceId => "LIFT01";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var db1 = ctx.RawStruct as DB1_StatusBlock;
//         if (db1 == null) return null;
//         if (db1.LIFT01_Fault) return SignalValidationResult.Reject("LIFT01 故障");
//         var prev = ctx.PreviousStruct as DB1_StatusBlock;
//         if (prev != null && prev.LIFT01_Idle && !db1.LIFT01_Idle)
//             return SignalValidationResult.Reject("LIFT01 已被占用");
//         if (ctx.Db?.Queryable<object>()
//             .Where("DeviceId = 'LIFT01' AND InMaintenance = 1").Any() == true)
//             return SignalValidationResult.Reject("LIFT01 维护中");
//         return SignalValidationResult.Pass("允许操作");
//     }
// }

// (5) ASRS01 堆垛机 — 忙→故障→非自动→库位满 四级验证
// public class Asrs01_Validator : ISignalValidator
// {
//     public string ValidatorId => "ASRS01_Store";
//     public string? DeviceId => "ASRS01";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var db2 = ctx.RawStruct as DB2_MachineBlock;
//         if (db2 == null) return null;
//         if (db2.ASRS01_Busy)    return SignalValidationResult.Defer("忙", 5000);
//         if (db2.ASRS01_Fault)   return SignalValidationResult.Reject("故障");
//         if (!db2.ASRS01_AutoMode) return SignalValidationResult.Reject("非自动模式");
//         if (ctx.Db?.Queryable<object>()
//             .Where("StorageId = @s AND Occupied = 1", new { s = $"ASRS01_{db2.ASRS01_TaskId:D4}" }).Any() == true)
//             return SignalValidationResult.Reject("库位已满");
//         return SignalValidationResult.Pass("允许入库");
//     }
// }

// (6) ROBOT01 机器人 — 无故障 + 有托盘 + 不在忙
// public class Robot01_Validator : ISignalValidator
// {
//     public string ValidatorId => "ROBOT01_Grip";
//     public string? DeviceId => "ROBOT01";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var r = ctx.RawStruct as PLC3_RobotBlock;
//         if (r == null) return null;
//         if (r.ROBOT01_Fault)         return SignalValidationResult.Reject("故障");
//         if (!r.ROBOT01_PalletPresent) return SignalValidationResult.Reject("无托盘");
//         if (r.ROBOT01_Busy && ctx.PreviousStruct is PLC3_RobotBlock p && p.ROBOT01_Busy)
//             return SignalValidationResult.Defer("执行中", 2000);
//         return SignalValidationResult.Pass("允许抓取");
//     }
// }

// (7) SORTER01 分拣线 — 下游 CV03 空闲才能启动
// public class Sorter01_Validator : ISignalValidator
// {
//     public string ValidatorId => "SORTER01_Start";
//     public string? DeviceId => "SORTER01";
//     public string? SignalId => null;
//     public SignalValidationResult? Validate(ValidatorContext ctx)
//     {
//         var r = ctx.RawStruct as PLC3_RobotBlock;
//         if (r == null) return null;
//         if (r.SORTER01_Fault) return SignalValidationResult.Reject("分拣线故障");
//         var ds = ctx.StateCenter.GetDeviceState("CV03");
//         if (ds?.Status == DeviceStatusEnum.Running)
//             return SignalValidationResult.Defer("下游 CV03 忙", 3000);
//         return SignalValidationResult.Pass("允许启动");
//     }
// }


// ====================================================================
// 四、验证器注册方式
// ====================================================================
// AddWcsPlc 扩展点中注册：
//   services.AddSingleton(sp =>
//   {
//       var svc = sp.GetRequiredService<S7PollingService>();
//       svc.RegisterValidator(new Cv01_ArrivalValidator());
//       svc.RegisterValidator(new Cv02_TransferValidator());
//       svc.RegisterValidator(new Cv03_MergeValidator());
//       svc.RegisterValidator(new Lift01_Validator());
//       svc.RegisterValidator(new Asrs01_Validator());
//       svc.RegisterValidator(new Robot01_Validator());
//       svc.RegisterValidator(new Sorter01_Validator());
//       return svc;
//   });
