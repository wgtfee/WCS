# 3 PLC 完整链路代码说明

> 本文档覆盖：PLC struct 定义 → 轮询读取 → StateCenter 同步 → 边沿检测 → 验证器 → 任务生成 → DAG 执行 → PLC 写入
> 包含 3 个 PLC、9 个 DB 块、18 个验证器、3 个写命令的完整链路。

---

## 目录

1. [PLC 配置总览](#1-plc-配置总览)
2. [PLC1 输送线代码详解](#2-plc1-输送线代码详解)
3. [PLC2 堆垛机代码详解](#3-plc2-堆垛机代码详解)
4. [PLC3 机器人代码详解](#4-plc3-机器人代码详解)
5. [轮询读取链路](#5-轮询读取链路)
6. [StateCenter 同步](#6-statecenter-同步)
7. [EventDetector 边沿检测](#7-eventdetector-边沿检测)
8. [18 个验证器详解](#8-18-个验证器详解)
9. [任务生成与 DAG 执行](#9-任务生成与-dag-执行)
10. [PLC 写入链路](#10-plc-写入链路)
11. [数据库持久化](#11-数据库持久化)
12. [完整链路示例](#12-完整链路示例)

---

## 1. PLC 配置总览

| PLC | 名称 | 地址 | DB1(状态) | DB2(请求) | DB3(报警) | 写命令块 |
|-----|------|------|-----------|-----------|-----------|---------|
| 1 | 输送线 | 192.168.0.1 | 10 站 | 10 站请求 | 10 站报警 | DB101 |
| 2 | 堆垛机 | 192.168.0.2 | 4 台 | 4 台请求 | 4 台报警 | DB201 |
| 3 | 机器人 | 192.168.0.3 | 4 台 | 4 台请求 | 4 台报警 | DB101 |

### appsettings.json 配置

```json
{
  "PlcConnections": [
    { "PlcName":"PLC1","Address":"192.168.0.1","Rack":0,"Slot":1 },
    { "PlcName":"PLC2","Address":"192.168.0.2","Rack":0,"Slot":1 },
    { "PlcName":"PLC3","Address":"192.168.0.3","Rack":0,"Slot":1 }
  ],
  "PlcBlocks": [
    { "PlcName":"PLC1", "BlockNumber":1, "Length":40, "PollIntervalMs":100,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC1_DB1_ConveyorStatus, Wcs.Core" },
    { "PlcName":"PLC1", "BlockNumber":2, "Length":20, "PollIntervalMs":100,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC1_DB2_ConveyorRequest, Wcs.Core" },
    { "PlcName":"PLC1", "BlockNumber":3, "Length":20, "PollIntervalMs":200,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC1_DB3_ConveyorAlarm, Wcs.Core" },
    { "PlcName":"PLC2", "BlockNumber":1, "Length":24, "PollIntervalMs":200,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC2_DB1_StackerStatus, Wcs.Core" },
    { "PlcName":"PLC2", "BlockNumber":2, "Length":24, "PollIntervalMs":200,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC2_DB2_StackerRequest, Wcs.Core" },
    { "PlcName":"PLC3", "BlockNumber":1, "Length":16, "PollIntervalMs":150,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC3_DB1_RobotStatus, Wcs.Core" },
    { "PlcName":"PLC3", "BlockNumber":2, "Length":16, "PollIntervalMs":150,
      "StructType":"Wcs.Core.PlcSubsystem.Examples.PLC3_DB2_RobotRequest, Wcs.Core" }
  ]
}
```

---

## 2. PLC1 输送线代码详解

**文件：** `PlcSubsystem/Examples/Plc1_ConveyorModels.cs`

### DB1: 输送线状态 (40 字节, 10 站 × 4 字节)

每个站占 4 字节：

```
byte 0:  bool DriveReady (bit 0), bool PalletArrived (bit 1), bool Fault (bit 2), bool Busy (bit 3)
byte 2:  short Speed（速度）
```

代码：
```csharp
public struct PLC1_DB1_ConveyorStatus
{
    public bool CV01_DriveReady;        // DB1.DBX0.0
    public bool CV01_PalletArrived;     // DB1.DBX0.1
    public bool CV01_Fault;             // DB1.DBX0.2
    public bool CV01_Busy;              // DB1.DBX0.3
    public short CV01_Speed;            // DB1.DBW2
    // ... 站 2~10 同理，每站偏移 +4
}
```

**解析方式：** `Struct.FromBytes<PLC1_DB1_ConveyorStatus>(bytes)` 按字段顺序自动填充。

### DB2: 输送线任务请求 (20 字节, 10 站 × 2 字节)

每个站占 2 字节：
```
byte 0:  bool RequestOut (bit 0), bool RequestIn (bit 1)
byte 1:  byte TargetStation
```

`RequestOut` 的 **上升沿**（false→true）触发 `PalletArrivedEvent`，进入任务流程。

### DB3: 输送线报警 (20 字节, 10 站 × 2 字节)

每个站占 2 字节：
```
byte 0:  bool Alarm (bit 0)
byte 1:  byte AlarmCode
```

### 写命令: ConveyorControlCommand

```csharp
[PlcBlock("PLC1", 101)]
public struct ConveyorControlCommand
{
    [PlcOffset(0, 0)] public bool StartStation1;
    [PlcOffset(0, 1)] public bool StopStation1;
    [PlcOffset(2)]    public short SpeedSetpoint1;
    // ...
}
```

**写入方式：** `cmdCenter.SendStructuredCommandAsync("CV01", "Start", cmd)` 自动从 `[PlcBlock("PLC1",101)]` 识别目标。

---

## 3. PLC2 堆垛机代码详解

**文件：** `PlcSubsystem/Examples/Plc2_StackerModels.cs`

### DB1: 堆垛机状态 (24 字节, 4 台 × 6 字节)

```csharp
public struct PLC2_DB1_StackerStatus
{
    public bool ASRS01_Busy;            // DB1.DBX0.0
    public bool ASRS01_Fault;           // DB1.DBX0.1
    public bool ASRS01_AutoMode;        // DB1.DBX0.2
    public bool ASRS01_PositionArrived; // DB1.DBX0.3
    public short ASRS01_CurColumn;      // DB1.DBW2
    public short ASRS01_CurRow;         // DB1.DBW4
    // ... 台 2~4 同理
}
```

### DB2: 堆垛机任务请求 (24 字节)

`StoreReq` 上升沿 → 入库请求事件 → 生成入库任务。

### DB3: 堆垛机报警 (12 字节)

### 写命令: StackerControlCommand

```csharp
[PlcBlock("PLC2", 201)]
public struct StackerControlCommand
{
    [PlcOffset(0, 0)] public bool StoreCmd1;
    [PlcOffset(0, 1)] public bool RetrieveCmd1;
    [PlcOffset(2)]    public short TargetCol1;
    [PlcOffset(4)]    public short TargetRow1;
}
```

---

## 4. PLC3 机器人代码详解

**文件：** `PlcSubsystem/Examples/Plc3_RobotModels.cs`

### DB1: 机器人状态 (16 字节, 4 台 × 4 字节)

```csharp
public struct PLC3_DB1_RobotStatus
{
    public bool ROBOT01_Busy;           // DB1.DBX0.0
    public bool ROBOT01_Gripped;        // DB1.DBX0.1
    public bool ROBOT01_Fault;          // DB1.DBX0.2
    public bool ROBOT01_PalletPresent;  // DB1.DBX0.3
    public short ROBOT01_AxisPos;       // DB1.DBW2
    // ... 台 2~4 同理
}
```

### DB2: 机器人任务请求 (16 字节)

`GripReq` 上升沿 → 抓取请求事件 → 生成机器人任务。

### 写命令: RobotControlCommand

```csharp
[PlcBlock("PLC3", 101)]
public struct RobotControlCommand
{
    [PlcOffset(0, 0)] public bool GripCmd1;
    [PlcOffset(0, 1)] public bool ReleaseCmd1;
    [PlcOffset(2)]    public short TargetPos1;
    // ...
}
```

---

## 5. 轮询读取链路

**文件：** `PlcSubsystem/S7/S7PollingService.cs`

每个 PlcBlock 启动一个独立 Timer，循环执行：

```
ReadPool.Get("PLC1").ReadAsync(DB1, 0, 40) → byte[40]
  → Struct.FromBytes(typeof(PLC1_DB1_ConveyorStatus), data, 40, 0)
  → PLC1_DB1_ConveyorStatus { CV01_DriveReady, CV01_PalletArrived, ... }

ReadPool.Get("PLC2").ReadAsync(DB1, 0, 24) → byte[24]
  → PLC2_DB1_StackerStatus { ASRS01_Busy, ASRS01_Fault, ... }

ReadPool.Get("PLC3").ReadAsync(DB1, 0, 16) → byte[16]
  → PLC3_DB1_RobotStatus { ROBOT01_Busy, ROBOT01_Fault, ... }
```

完整代码流程（`S7PollingService.Start()`）：

```csharp
foreach (var reg in _registry.GetAll())
{
    var timer = new Timer(async _ =>
    {
        // 1. 读 PLC 原始字节
        var (data, result, error) = await conn.ReadAsync(
            reg.BlockNumber, reg.StartByte, reg.Length);

        // 2. byte[] → 强类型 struct
        var current = Struct.FromBytes(reg.StructType, data, reg.Length, 0);

        // 3. StateCenter 无条件同步（0 验证）
        SyncStateCenter(reg.StructType, current);

        // 4. SignalSnapshotCenter 更新
        _snapshotCenter.Update(blockKey, current, reg.StructType);

        // 5. EventDetector 边沿检测 → 两级事件
        _eventDetector.Detect(blockKey, current, reg.PlcName, reg.BlockNumber);
    }, null, 0, reg.PollIntervalMs);
}
```

---

## 6. StateCenter 同步

**文件：** `S7PollingService.SyncStateCenter()`

```csharp
private void SyncStateCenter(Type structType, object current)
{
    var fields = FieldMetadataCache.GetFields(structType);
    foreach (var meta in fields)
    {
        var newVal = FieldMetadataCache.GetValue(meta, current);
        if (meta.DeviceId == null) continue;

        // 从字段值推断设备状态
        var status = newVal is bool b && b ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle;

        // 无条件更新 StateCenter（不经过验证器）
        _stateCenter.UpdateDeviceState(meta.DeviceId, new DeviceState
        {
            DeviceId = meta.DeviceId,
            Status = status,
            LastUpdateTime = DateTime.UtcNow
        });
    }
}
```

**核心原则：** StateCenter 永远同步 PLC 真实状态，验证器拒绝不影响 StateCenter。

### StateCenter 同步后的数据

```
StateCenter 中的内容（业务状态）:
  CV01:  { Status=Running }      (因为 CV01_DriveReady = true)
  CV02:  { Status=Idle }          (因为 CV02_DriveReady = false)
  ASRS01: { Status=Running }      (因为 ASRS01_Busy = true)
  ROBOT01: { Status=Running }     (因为 ROBOT01_Busy = true)
```

**注意：** StateCenter 只存业务状态（Running/Idle），不存 PLC 原始数据（CurrentStruct/PreviousStruct）。原始数据在 SignalSnapshotCenter 中。

---

## 7. EventDetector 边沿检测

**文件：** `EventDetection/EventDetector.cs`

### 两级事件管线

```
PLC 字段变化
  ↓
RawSignalEvent（始终发布，含 ValidatorPassed 状态）
  ↓
Validator 管道（18 个验证器依次执行）
  ├─ 全部 Pass → 发布 DomainEvent（如 PalletArrivedEvent）
  └─ 任一 Reject → 不发布 DomainEvent（StateCenter 已更新，不受影响）
```

### 命名约定推断

字段名后缀自动决定事件类型（零配置）：

| 字段名 | 上升沿产生的事件 | 说明 |
|--------|---------------|------|
| `CV01_PalletArrived` | `PalletArrivedEvent` | 托盘到位 |
| `CV01_RequestOut` | `PalletArrivedEvent` | 请求出站 |
| `CV01_Fault` | `DeviceFaultEvent` | 设备故障 |
| `ASRS01_StoreReq` | `PalletArrivedEvent` | 入库请求 |
| `ASRS01_RetrieveReq` | `PalletArrivedEvent` | 出库请求 |
| `ROBOT01_GripReq` | `PalletArrivedEvent` | 抓取请求 |

### RawSignalEvent 结构

```csharp
public class RawSignalEvent : EventBase
{
    public string PlcName;          // "PLC1"
    public int DbBlock;             // 1
    public string FieldName;        // "CV01_PalletArrived"
    public string? OldValue;        // "false"
    public string? NewValue;        // "true"
    public string Edge;             // "Rising"
    public bool ValidatorPassed;    // true/false
    public string? ValidatorReason; // 验证器拒绝原因
    public string? DomainEventType; // "PalletArrivedEvent"
}
```

TraceCenter 记录此事件后，排查问题时：

```
09:00:01  PLC1.DB1.CV01_RequestOut  false→true  Rising  ✅ Pass → PalletArrivedEvent
09:00:01  PLC1.DB1.CV01_Fault       false→true  Rising  ❌ Reject("CV01 故障中") → NO DomainEvent
```

---

## 8. 18 个验证器详解

**文件：** `PlcSubsystem/Examples/AllStationValidators.cs`

### 输送线 10 个

| 验证器 | 设备 | 验证逻辑 |
|--------|------|---------|
| `Cv01_ArrivalValidator` | CV01 | 故障→Reject，未就绪→Reject，查数据库维护状态 |
| `Cv02_TransferValidator` | CV02 | 故障→Reject，繁忙→Defer，上游 CV01 运输中→Defer |
| `Cv03_MergeValidator` | CV03 | 故障→Reject，LIFT01 忙或合流占用→Defer |
| `Cv04_BufferValidator` | CV04 | 故障→Reject，其余通过 |
| `Cv05_WeighValidator` | CV05 | 故障→Reject |
| `Cv06_SortEntryValidator` | CV06 | 故障→Reject，分拣机未空闲→Defer |
| `Cv07_OutboundValidator` | CV07 | 故障→Reject |
| `Cv08_LiftEntryValidator` | CV08 | 故障→Reject，查 StateCenter 中 LIFT01 状态→Defer |
| `Cv09_StorageEntryValidator` | CV09 | 故障→Reject |
| `Cv10_ExitValidator` | CV10 | 故障→Reject |

### 堆垛机 4 个

| 验证器 | 设备 | 验证逻辑 |
|--------|------|---------|
| `Asrs01_Validator` | ASRS01 | 故障→Reject，繁忙→Defer(5s)，非自动→Reject |
| `Asrs02_Validator` | ASRS02 | 同上 |
| `Asrs03_Validator` | ASRS03 | 同上 |
| `Asrs04_Validator` | ASRS04 | 同上 |

### 机器人 4 个

| 验证器 | 设备 | 验证逻辑 |
|--------|------|---------|
| `Robot01_Validator` | ROBOT01 | 故障→Reject，无托盘→Reject，持续繁忙→Defer(2s) |
| `Robot02_Validator` | ROBOT02 | 同上 |
| `Robot03_Validator` | ROBOT03 | 同上 |
| `Robot04_Validator` | ROBOT04 | 同上 |

### 验证器代码示例

```csharp
// 以 CV01 为例——验证器通过 ctx 获取三类数据
public SignalValidationResult? Validate(ValidatorContext ctx)
{
    // 1. 从 RawStruct 读取当前 PLC 数据（强类型）
    if (ctx.RawStruct is not PLC1_DB1_ConveyorStatus db1) return null;

    // 2. 从 StateCenter 读取其他设备状态
    var lift = ctx.StateCenter.GetDeviceState("LIFT01");

    // 3. 从数据库查询（ISqlSugarClient）
    if (ctx.Db?.Queryable<object>().Where("StationId='CV01' AND InMaintenance=1").Any() == true)
        return SignalValidationResult.Reject("维护中");

    // 4. 返回结果
    if (db1.CV01_Fault) return SignalValidationResult.Reject("故障");
    return SignalValidationResult.Pass("允许");
}
```

---

## 9. 任务生成与 DAG 执行

### RuleEngine 规则示例

EventDetector 发布 `PalletArrivedEvent` 后，RuleEngine 匹配规则：

```
规则 "CV01 到位触发运输到提升机":
  触发: PalletArrivedEvent.DeviceId == "CV01"
  检查: StateCenter("LIFT01").Status == Idle
  检查: StateCenter("CV01").Status == Running
  动作: 发布 TransportRequestedEvent
        { SourceDeviceId = "CV01", TargetDeviceId = "LIFT01" }
```

### TaskGenerator 消费 TransportRequestedEvent

```csharp
_eventBus.Subscribe<TransportRequestedEvent>(async (evt, ct) =>
{
    var task = new TaskContext
    {
        DeviceId = evt.SourceDeviceId,
        RouteId = $"{evt.SourceDeviceId}→{evt.TargetDeviceId}",
        Tags = { ["PalletId"] = evt.PalletId ?? "", ["FromNode"] = evt.SourceDeviceId }
    };
    await _scheduler.EnqueueAsync(task, ct);
});
```

### DAG 图定义（运输任务从 CV01 到 ASRS01）

```csharp
var graph = ChainBuilder.Create()
    .AddAction("start_cv01", "StartConveyor")
    .AddWait("wait_cv01_ok", new WaitCondition { DeviceId = "CV01", ExpectedStatus = "Ready" })
        .DependsOn("wait_cv01_ok", "start_cv01")
    .AddAction("request_lift", "MoveLift")
        .DependsOn("request_lift", "wait_cv01_ok")
    .AddWait("wait_lift_done", new WaitCondition { DeviceId = "LIFT01", ExpectedStatus = "Ready" })
        .DependsOn("wait_lift_done", "request_lift")
    .AddAction("store_asrs", "StoreToAsrs")
        .DependsOn("store_asrs", "wait_lift_done")
    .Build();
```

### DAG 执行时 ActionNode 写入 PLC

```csharp
// ActionNode "StartConveyor" 执行时：
await cmdCenter.SendStructuredCommandAsync("CV01", "Start", new ConveyorControlCommand
{
    StartStation1 = true,
    SpeedSetpoint1 = 1500
});
// 自动: [PlcBlock("PLC1",101)] → WritePool("PLC1") → DB101.DBX0.0=1, DB101.DBW2=1500
```

---

## 10. PLC 写入链路

### 完整写入链路

```
ChainExecutionEngine.ActionNode
  ↓
CommandCenter.SendStructuredCommandAsync(deviceId, commandType, commandStruct)
  ↓
PlcWriter.WriteStructAsync(commandStruct)
  ↓
读取 struct 上的 [PlcBlock("PLC1", 101)] 特性
  ↓
PlcSerializer.Serialize(commandStruct, bufferSize)
  → 按 [PlcOffset] 特性将每个字段写入 byte[]
  → PlcOffset(0,0) → byte[0].bit0
  → PlcOffset(2)   → byte[2..3]
  ↓
WritePool.Get("PLC1").WriteAsync(DB101, 0, byte[])
  ↓
S7Client.WriteArea() → PLC 硬件
```

### 写命令示例

```csharp
// 输送线启动
await cmdCenter.SendStructuredCommandAsync("CV01", "Start",
    new ConveyorControlCommand { StartStation1 = true, SpeedSetpoint1 = 1500 });
// → PLC1.DB101.DBX0.0 = 1, DB101.DBW2 = 1500

// 堆垛机入库
await cmdCenter.SendStructuredCommandAsync("ASRS01", "Store",
    new StackerControlCommand { StoreCmd1 = true, TargetCol1 = 15, TargetRow1 = 8 });
// → PLC2.DB201.DBX0.0 = 1, DB201.DBW2 = 15, DB201.DBW4 = 8

// 机器人抓取
await cmdCenter.SendStructuredCommandAsync("ROBOT01", "Grip",
    new RobotControlCommand { GripCmd1 = true, TargetPos1 = 3 });
// → PLC3.DB101.DBX0.0 = 1, DB101.DBW2 = 3
```

---

## 11. 数据库持久化

### 验证器查数据库

验证器通过 `ctx.Db`（ISqlSugarClient）查库：

```csharp
if (ctx.Db?.Queryable<object>()
    .Where("StationId='CV01' AND InMaintenance=1").Any() == true)
    return SignalValidationResult.Reject("维护中");
```

### 任务状态写入数据库

StateCenter 中的 `TaskStateManager` 负责维护任务运行时状态，PersistBackgroundService 定期将 StateCenter 数据持久化到 SqlSugar。

### 运输历史写入 ExecutionHistoryCenter

```csharp
// 任务完成后：
execHistory.CreateRecord(taskId, palletId, sourceNode, targetNode);
execHistory.RecordNodeArrival(taskId, nodeId);
execHistory.CompleteRecord(taskId, true);
```

### EventBus 持久化

所有关键事件通过 `FileEventStore`（JSON-lines）落盘：

```
events_20260604_09.jsonl  ← RawSignalEvent + DomainEvent + DeviceStateChangedEvent
```

---

## 12. 完整链路示例

### 场景：托盘从 CV01 输送到 ASRS01

```
时间轴：
  ① 09:00:00.000  S7PollingService 轮询 PLC1.DB1
       ReadPool("PLC1").ReadAsync(DB1, 0, 40) → byte[40]
       Struct.FromBytes<PLC1_DB1_ConveyorStatus>(bytes)
       → CV01_PalletArrived = true（上升沿）

  ② 09:00:00.001  StateCenter 无条件同步
       UpdateDeviceState("CV01", Running)        ← 即时更新
       UpdateDeviceState("CV02", Idle)
       UpdateDeviceState("CV03", Idle)

  ③ 09:00:00.002  EventDetector 边沿检测
       CV01_PalletArrived: false→true → 上升沿
       → 发布 RawSignalEvent { Field="CV01_PalletArrived", Edge="Rising" }
       → Validator 管道:
           Cv01_ArrivalValidator: ✅ Pass → 发布 PalletArrivedEvent

  ④ 09:00:00.010  RuleEngine 收到 PalletArrivedEvent
       规则匹配:
         IF DeviceId="CV01" AND LIFT01.Idle AND ASRS01.Idle
         → 发布 TransportRequestedEvent { SourceDeviceId="CV01", TargetDeviceId="ASRS01" }

  ⑤ 09:00:00.015  TaskGenerator 收到 TransportRequestedEvent
       → 创建 TaskContext { RouteId = "CV01→ASRS01" }
       → TaskScheduler.Enqueue(task)

  ⑥ 09:00:00.020  ChainExecutionEngine 出队
       DAG 图执行:

       ┌─ ActionNode: StartConveyor
       │    CommandCenter.SendStructuredCommandAsync("CV01","Start",
       │        new ConveyorControlCommand { StartStation1=true, SpeedSetpoint1=1500 })
       │    → PlcWriter.WriteStructAsync(cmd)
       │    → [PlcBlock("PLC1",101)] → WritePool("PLC1").WriteAsync(DB101,0,byte[4])
       │    → PLC1.DB101.DBX0.0=1, DB101.DBW2=1500
       │
       ├─ WaitNode: "等待 CV01 到位"
       │    → EventBus 订阅 DeviceStateChangedEvent
       │    → 下一轮 (100ms 后) S7PollingService 读到 CV01 到位 → StateCenter 更新
       │
       ├─ ActionNode: MoveLift
       │    → [PlcBlock("PLC1",102)] → WritePool("PLC1").WriteAsync(DB102,...)
       │    → GoUp=1, TargetFloor=2
       │
       ├─ WaitNode: "等待 LIFT01 到位"
       │
       ├─ ActionNode: StoreToAsrs
       │    → [PlcBlock("PLC2",201)] → WritePool("PLC2").WriteAsync(DB201,...)
       │    → StoreCmd1=1, TargetCol=15, TargetRow=8
       │
       └─ WaitNode: "等待 ASRS01 完成"

  ⑦ 09:00:20.000  ExecutionHistoryCenter 记录
       Pallet: PALLET_0001
       Route: CV01 → CV02 → LIFT01 → ASRS01
       Nodes: CV01(3s), LIFT01(5s), ASRS01(8s)
       Total: 16s | Status: Completed
```

---

## 文件清单

| 文件路径 | 职责 |
|---------|------|
| `Examples/Plc1_ConveyorModels.cs` | PLC1 的 3 个 DB 块 struct（状态/请求/报警）+ 写命令 |
| `Examples/Plc2_StackerModels.cs` | PLC2 的 3 个 DB 块 struct + 写命令 |
| `Examples/Plc3_RobotModels.cs` | PLC3 的 3 个 DB 块 struct + 写命令 |
| `Examples/AllStationValidators.cs` | 全部 18 个验证器 |
| `EventDetection/EventDetector.cs` | 两级事件管线（RawSignalEvent + DomainEvent） |
| `EventDetection/FieldMetadataCache.cs` | 字段元数据缓存（启动时一次反射） |
| `EventDetection/EventDetectionRule.cs` | 检测规则模型 |
| `SignalSnapshot/SignalSnapshotCenter.cs` | PLC 块快照（Current/Previous/Version） |
| `PlcSubsystem/S7/S7PollingService.cs` | 轮询 → StateCenter → EventDetector |
| `CommandCenter/CommandProfile.cs` | 可配置命令状态机 |
| `docs/complete-3plc-flow.md` | 本文 |
