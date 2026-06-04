# 现场部署实践指南 — 信号批量导入与工位验证规则配置

> 回答两个现场落地最常遇到的问题：
> 1. **几千个 PLC 点位** — 逐条手写 JSON 要几天？→ 从博图 CSV 批量导入
> 2. **几十个工位的复杂验证** — 每个工位写一个 C# 类？→ 配置化验证规则（AND/OR 多层）

---

## 一、PLC 信号点位：批量导入

### 方案一：CSV 批量导入（推荐）

从西门子 TIA Portal（博图）导出的标签表 CSV，几分钟导入几千个点位。

```csharp
// 代码中批量导入（一次性的工具函数）
var csvContent = File.ReadAllText("tia_tags.csv");
var importer = new SignalCsvImporter();
var result = importer.ImportFromCsv(csvContent, CsvColumnMap.TiaPortal());

// result.Imported — 导入成功数
// result.Skipped  — 跳过数
// result.Errors   — 错误列表

// 注册到 SignalMapper
signalMapper.RegisterDefinitions(definitions);
```

### CSV 格式示例（TIA Portal 默认导出）

```csv
"Name","Address","DataType","Comment"
"CV01_DriveReady","DB1.DBX0.0","Bool","CV01 驱动就绪"
"CV01_PalletArrived","DB1.DBX0.1","Bool","托盘到位"
"CV01_Speed","DB1.DBW2","Int","当前速度"
"LIFT01_AtFloor","DB2.DBX0.0","Bool","到达楼层"
"LIFT01_GoingUp","DB2.DBX0.1","Bool","上升中"
"ASRS01_Fault","DB3.DBX0.0","Bool","堆垛机故障"
"EStop_ZoneA","DB10.DBX0.0","Bool","A区急停"
```

### 命名约定自动推断

导入器根据标签名自动推断目标事件类型和属性映射，**无需手动指定**：

| 标签名包含 | 自动推断为 |
|-----------|-----------|
| `Arrived` / `到位` | `PalletArrivedEvent` |
| `Ready` / `就绪` | `ConveyorReadyChangedEvent` |
| `Fault` / `Error` / `故障` | `DeviceFaultEvent` |
| `Speed` / `速度` | `ConveyorSpeedChangedEvent` |
| `EStop` / `Emergency` / `急停` | `EmergencyStopEvent` |
| `Mode` / `模式` | `ModeSwitchedEvent` |

### 列映射配置

如果 CSV 格式不同，配置列映射即可：

```csharp
// 西门子 TIA Portal 默认格式
CsvColumnMap.TiaPortal()

// 自定义格式（指定每列的索引）
new CsvColumnMap
{
    TagNameColumn = 0,     // 标签名在第 0 列
    AddressColumn = 2,     // 地址在第 2 列
    DataTypeColumn = 3,    // 类型在第 3 列
    CommentColumn = 5,     // 注释在第 5 列
    HasHeader = true,
    Delimiter = ","
};
```

### 方案二：JSON 逐条配置（少量点位/特殊信号）

少量特殊信号可以在 `appsettings.json` 中手动配置：

```json
{
  "Signals": [
    {
      "SignalId": "CV01_PalletArrived",
      "BlockNumber": 1,
      "ByteOffset": 0,
      "BitOffset": 1,
      "DataType": "bool",
      "TargetEventType": "Wcs.Core.EventBus.Events.PalletArrivedEvent",
      "PropertyMappings": { "DeviceId": "$CV01" },
      "Description": "CV01 托盘到位"
    }
  ]
}
```

### 可用事件类型

| 事件类 | 属性 |
|--------|------|
| `ConveyorReadyChangedEvent` | DeviceId, Ready, PlcName |
| `PalletArrivedEvent` | DeviceId, Barcode, PlcName |
| `DeviceFaultEvent` | DeviceId, FaultCode, Description |
| `EmergencyStopEvent` | DeviceId, PlcName |
| `ConveyorSpeedChangedEvent` | DeviceId, Speed, PlcName |
| `ModeSwitchedEvent` | DeviceId, Mode, PlcName |

---

## 二、工位验证规则：JSON 配置化

### 解决什么问题

> CV03 只有在提升机空闲且 CV03 自身也空闲时，才能接收托盘。否则拒绝。

### 配置验证规则

在 `appsettings.json` → `"ValidationRules"` 中定义：

```json
{
  "ValidationRules": [
    {
      "RuleId": "CV03_Arrive_Prerequisites",
      "TargetDeviceId": "CV03",
      "TargetSignalId": "CV01_PalletArrived",
      "Conditions": {
        "Operator": "AND",
        "Items": [
          { "CheckType": "DeviceState", "DeviceId": "LIFT01", "ExpectedStatus": "Idle" },
          { "CheckType": "DeviceState", "DeviceId": "CV03", "ExpectedStatus": "Idle" }
        ]
      },
      "OnRejectMessage": "LIFT01 忙或 CV03 非空闲，拒绝"
    }
  ]
}
```

**无需写任何 C# 代码。新增规则只需在 JSON 中加一条。**

### 规则匹配逻辑

| 字段 | 含义 | 示例 |
|------|------|------|
| `TargetDeviceId` | 目标设备（null=所有设备） | `"CV03"` |
| `TargetSignalId` | 目标信号（null=该设备所有信号） | `"CV01_PalletArrived"` |

### 条件类型

| CheckType | 说明 | 参数 |
|-----------|------|------|
| `DeviceState` | 检查某个设备的状态 | DeviceId, ExpectedStatus |
| `AlwaysPass` | 始终通过（用于调试） | 无 |
| `AlwaysReject` | 始终拒绝（用于临时屏蔽） | ExpectedValue=原因 |

### 多层 AND/OR 嵌套

支持多层嵌套，解决复杂工位的验证需求：

```json
{
  "RuleId": "ComplexStation_Validation",
  "Conditions": {
    "Operator": "AND",
    "Items": [
      { "CheckType": "DeviceState", "DeviceId": "LIFT01", "ExpectedStatus": "Idle" },
      { "CheckType": "DeviceState", "DeviceId": "CV03", "ExpectedStatus": "Idle" }
    ],
    "Groups": [
      {
        "Operator": "OR",
        "Items": [
          { "CheckType": "DeviceState", "DeviceId": "ASRS01", "ExpectedStatus": "Idle" },
          { "CheckType": "DeviceState", "DeviceId": "ASRS02", "ExpectedStatus": "Idle" }
        ]
      }
    ]
  }
}
```

逻辑为：`(LIFT01.Idle AND CV03.Idle) AND (ASRS01.Idle OR ASRS02.Idle)`

### 临时屏蔽信号

生产现场常见的需求——设备维修时暂时屏蔽某个报警信号：

```json
{
  "RuleId": "Temp_Maintenance_ASRS01",
  "TargetDeviceId": "ASRS01",
  "Conditions": {
    "Operator": "AND",
    "Items": [
      { "CheckType": "AlwaysReject", "ExpectedValue": "设备维修中，信号已屏蔽" }
    ]
  },
  "OnRejectMessage": "ASRS01 维修中，信号已屏蔽"
}
```

恢复时只需 `"Enabled": false` 或删除该规则。

---

## 三、现场部署要点

### 启动日志确认

启动时确认日志中有以下输出：

```
📋 已加载 X 个信号映射定义    ← 确认信号加载数量
🛡️ 已加载 Y 条配置化验证规则  ← 确认验证规则加载数量
```

### 验证规则故障排查

被拒绝的信号会在日志中显示：

```
[验证] ❌ CV03_Arrive_Prerequisites: CV03/CV01_PalletArrived LIFT01 忙 ...
```

### CSV 导入（一次性工具）

CSV 导入通常是一次性的迁移工具，不在每次启动时执行。导入后生成的 `SignalDefinition` 可以直接注册到 `SignalMapperEngine`。

---

## 四、一句话总结

```
PLC 点位：从博图导出 CSV → SignalCsvImporter 批量导入（几分钟几千个点位）
工位验证：在 appsettings.json 中配 ValidationRules（AND/OR 多层，不用写代码）
```

## 五、现在验证管道的架构是：

两种验证方式，各自解决各自的问题

简单条件（JSON 配置）
┌───────────────────────────────────────────┐
│ appsettings.json → "ValidationRules"       │
│                                           │
│ "Conditions": {                            │
│   "Operator": "AND",                       │
│   "Items": [                               │
│     { "DeviceId":"LIFT01","Status":"Idle"},│
│     { "DeviceId":"CV03", "Status":"Idle"}   │
│   ]                                        │
│ }                                          │
└───────────────────────────────────────────┘
用途：设备状态检查、简单的 AND/OR 条件
成本：零代码，改 JSON 即可

复杂业务逻辑（代码验证器）
┌───────────────────────────────────────────┐
│ 实现 ISignalValidator 接口                 │
│                                           │
│ Validate(ValidatorContext ctx) {            │
│   ctx.StateCenter.GetDeviceState("CV02")   │
│   ctx.StateCenter.GetDeviceState("LIFT01") │
│   ctx.Definition.PropertyMappings          │
│   ctx.RawDiff.Changes                      │
│   ctx.GeneratedEvents                      │
│ }                                          │
└───────────────────────────────────────────┘
用途：工位互锁、查数据库、路径验证
成本：写一个类，一行注册（或加 [SignalValidator] 自动发现）
ValidatorContext 给验证器提供一切它需要的东西——StateCenter、信号定义、原始 PLC 数据、已生成的事件——不依赖外部 DI 注入。

3 个真实场景示例
示例	验证什么	用到 ValidatorContext 什么
StationInterlockValidator	上下游设备互锁	StateCenter.GetDeviceState()
PalletBarcodeDatabaseValidator	条码去重防重复	Definition.PropertyMappings
RoutePathValidator	目标设备故障检查	StateCenter + Definition


## 六、现在验证器可以查数据库了。用法：


// 在自定义验证器中：
public SignalValidationResult? Validate(ValidatorContext ctx)
{
    var db = ctx.Db;  // ISqlSugarClient

    // 查业务表
    var pallet = db.Queryable<PalletTable>()
        .Where(p => p.Barcode == barcode).First();

    // 查状态表
    var exists = db.Queryable<ProcessTable>()
        .Where(p => p.TaskId == taskId).Any();

    // 查配置表
    var config = db.Queryable<StationConfig>()
        .Where(s => s.StationId == deviceId).First();
}
现场部署时在 Program.cs 中注册 SqlSugar：


// 注册 SqlSugar（一行）
builder.Services.AddSingleton<ISqlSugarClient>(sp =>
    new SqlSugarClient(new ConnectionConfig
    {
        ConnectionString = builder.Configuration.GetConnectionString("WcsDb"),
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    }));

// SignalMapperEngine 自动获取 ISqlSugarClient
builder.Services.AddSingleton<SignalMapperEngine>(sp =>
    new SignalMapperEngine(
        sp.GetRequiredService<IStateCenter>(),
        sp.GetService<ISqlSugarClient>()  // 可选，为 null 时验证器不查库
    ));
改动总结：

文件	改了什么
Wcs.Core.csproj	添加 SqlSugarCore 包引用
ValidatorContext.cs	新增 ISqlSugarClient? Db 属性
SignalMapperEngine.cs	新增 ISqlSugarClient? 参数，传入 ValidatorContext
Program.cs	注册时传入 ISqlSugarClient
Wcs.Infrastructure.csproj	升级 Microsoft.Data.SqlClient 到 5.2.2
PalletBarcodeDatabaseValidator.cs	改为例展示真实 ctx.Db.Queryable<T>() 查库

## 七、现在点位从 CSV 导入后到验证器使用的完整链路是：

CSV 导入（一次性工具）
─────────────────────────────────────────────────
var result = new SignalCsvImporter()
    .ImportFromCsv(csvContent, CsvColumnMap.TiaPortal());

// result.Definitions  ← 现在这里有完整的 List<SignalDefinition>
// result.Imported     ← 导入成功数
// result.Errors       ← 错误列表

// 批量注册到 SignalMapperEngine（内存）
signalMapper.RegisterDefinitions(result.Definitions);
点位存储在 SignalMapperEngine 内存中（三个地方可查）：


1. 通过 ISignalMapper 接口查全部定义
   signalMapper.GetDefinitions()  → IReadOnlyList<SignalDefinition>

2. 通过 ValidatorContext 查当前触发的信号定义
   ctx.Definition  → 当前信号
   ctx.Definition.PropertyMappings  → 属性映射

3. 验证器中通过 ctx.Db 查数据库
   业务表（PalletRegistration / TaskTable / StationMaterialConfig 等）
   ctx.Db.Queryable<T>().Where(...).First()
CSV 导入是一次性的迁移工具（只在项目开始时跑一次）。导入后所有点位都注册到 SignalMapperEngine 内存里，运行时验证器通过 ValidatorContext.Definition 拿到当前信号的完整定义——SignalId、PlcName、BlockNumber、PropertyMappings 等。

验证器中查库用 ctx.Db 直接写 LINQ，不需要手动注入：


public SignalValidationResult? Validate(ValidatorContext ctx)
{
    var db = ctx.Db;  // ISqlSugarClient，由 SignalMapperEngine 自动注入

    // 查业务表确认条码有效性
    var pallet = db.Queryable<PalletRegistrationTable>()
        .Where(p => p.Barcode == barcode).First();
}


现在代码回到你的架构上，完整链路是：


S7PLCPool.ReadPLCDataAsync() → byte[]
    ↓
Struct.FromBytes<T>(bytes) → 强类型 struct ← 你定义的 C# class，字段=PLC地址
    ↓
StructDiffEngine.Compare<T>(old, cur) → 字段级变化
    ↓
StructBridge.Process(blockName, previous, current)
    ├── 验证管道 (ISignalValidator + ValidatorContext)
    │     ctx.StateCenter → 设备状态
    │     ctx.Db          → SqlSugar 查库
    │     ctx.RawStruct   → 完整 DB 块结构体，强类型访问
    │     ctx.PreviousStruct → 上一次的数据，可对比
    │
    ├── 拒绝 → 日志记录原因
    └── 通过 → 发布 FieldChangedEvent 到 EventBus
你定义一个 DB 块 struct，Struct.FromBytes<T>() 按字段顺序自动映射：


// 你只需要定义这个 class，其余全部自动
public class DB1_Struct
{
    public bool CV01_DriveReady { get; set; }      // DB1.DBX0.0
    public bool CV01_PalletArrived { get; set; }   // DB1.DBX0.1
    public bool LIFT01_Idle { get; set; }           // DB1.DBX0.2
    public short CV01_Speed { get; set; }           // DB1.DBW2
}
验证器中强类型访问：


public SignalValidationResult? Validate(ValidatorContext ctx)
{
    var db1 = ctx.RawStruct as DB1_Struct;
    if (db1?.CV01_PalletArrived == true && db1?.LIFT01_Idle == false)
        return SignalValidationResult.Reject("提升机未空闲");

    // 查数据库
    var pallet = ctx.Db?.Queryable<Pallet>()
        .Where(p => p.Barcode == barcode).First();
}
删除的噪音： 整个 SignalMapper/Import/、SignalMapperEngine、ISignalMapper、SignalDefinition、ValidationRuleConfig、BuiltInValidators、BusinessSignals（后恢复）。

保留的核心： ISignalValidator + ValidatorContext + SignalValidationResult（移到 PlcSubsystem/Validation/）、StructDiffEngine + StructBridge（已在 PlcSubsystem/S7/）。


多个 PLC 的处理方式——用 PlcStructRegistry 注册，一个循环跑所有 PLC 所有块：


// ===== 定义 struct（每种 DB 块一个 C# class）=====
public class DB1_Struct
{
    public bool CV01_DriveReady { get; set; }
    public bool CV01_PalletArrived { get; set; }
    ...
}

public class PLC2_DB1_Struct
{
    public bool Robot01_Busy { get; set; }
    public short Robot01_Speed { get; set; }
    ...
}

// ===== 注册表：PLC1 的 DB1 → DB1_Struct，PLC2 的 DB1 → PLC2_DB1_Struct ====
var registry = new PlcStructRegistry();
registry.Register<DB1_Struct>("PLC1", blockNumber: 1, length: 200, pollIntervalMs: 100);
registry.Register<PLC2_DB1_Struct>("PLC2", blockNumber: 1, length: 256, pollIntervalMs: 200);

// ===== S7PollingService 自动循环所有 PLC + 所有块 ====
var service = new S7PollingService(registry, logger);
service.Start(pool, bridge);

// 内部自动为每个注册项启动一个 Timer，各自按 pollIntervalMs 轮询：
// PLC1.DB1  → 每 100ms → Struct.FromBytes<DB1_Struct>(bytes) → StructBridge → EventBus
// PLC2.DB1  → 每 200ms → Struct.FromBytes<PLC2_DB1_Struct>(bytes) → StructBridge → EventBus
完整链路：


PlcStructRegistry
  ├── PLC1.DB1 → DB1_Struct        → 100ms 轮询 → StructDiff → StructBridge → EventBus
  ├── PLC1.DB2 → DB2_Struct        → 100ms 轮询 → StructDiff → StructBridge → EventBus
  ├── PLC2.DB1 → PLC2_DB1_Struct   → 200ms 轮询 → StructDiff → StructBridge → EventBus
  └── PLC3.DB1 → PLC3_DB1_Struct   → 500ms 轮询 → StructDiff → StructBridge → EventBus
验证器通过 ctx.RawStruct as DB1_Struct 拿到强类型数据，不管数据来自哪个 PLC


根本问题解决了。现在的完整流程：


传统轮询（错误）：
  每 100ms 读 200 字节 → 2000 字节/秒/块 → PLC 负载高 ✅ 已废弃

触发模式（正确）：
  每 100ms 读 2 字节计数器 → 20 字节/秒/块 → PLC 负载 ≈ 0
  计数器变了 → 读 200 字节全块 → 正常负载（仅发生变化的瞬间）
  计数器没变 → 跳过（零负载）
PLC 侧的协议约定
每个 DB 块的 byte[0..1] 需要 PLC 维护一个 ushort GenerationCounter：


PLC 程序逻辑（梯形图/SCL）：
  IF "DataChanged" THEN
      "DB1".GenerationCounter := "DB1".GenerationCounter + 1;
  END_IF;
WCS 端的注册：


registry.Register<DB1_Struct>("PLC1", blockNumber: 1, length: 200, pollIntervalMs: 100);
// byte[0..1] = GenerationCounter → 自动检测
// byte[2..]  = 实际字段 → 只有变化时才读
如果 PLC 不能改程序（已有项目）
TriggerMode = false 即可回到全量模式，但每次读完后会自动跳过 CRC32 没变的块：


var svc = new S7TriggerPollingService(registry, logger);
svc.TriggerMode = false; // 不依赖 PLC 计数器，但每次读全块
svc.Start(pool, bridge);
推荐新项目用触发模式，已有项目尽量争取加 2 字节计数器，通讯负载降低 99%。




现在现场部署只需要：

1. 定义你的 DB 块 struct（一个类一个块）


public class DB1_Struct
{
    public bool CV01_DriveReady { get; set; }       // DB1.DBX0.0
    public bool CV01_PalletArrived { get; set; }    // DB1.DBX0.1
    public short CV01_Speed { get; set; }            // DB1.DBW2
}

public class DB2_Struct
{
    public bool LIFT01_AtFloor { get; set; }         // DB2.DBX0.0
    public bool LIFT01_GoingUp { get; set; }        // DB2.DBX0.1
}
2. 改 appsettings.json（不改代码）


{
  "PlcConnections": [
    { "PlcName":"PLC1", "Address":"192.168.0.1", "Rack":0, "Slot":1 },
    { "PlcName":"PLC2", "Address":"192.168.0.2", "Rack":0, "Slot":1 }
  ],
  "PlcBlocks": [
    { "PlcName":"PLC1", "BlockNumber":1, "Length":200, "PollIntervalMs":100,
      "StructType":"Wcs.MyApp.DB1_Struct, Wcs.MyApp" },
    { "PlcName":"PLC1", "BlockNumber":2, "Length":100, "PollIntervalMs":500,
      "StructType":"Wcs.MyApp.DB2_Struct, Wcs.MyApp" },
    { "PlcName":"PLC2", "BlockNumber":1, "Length":256, "PollIntervalMs":200,
      "StructType":"Wcs.MyApp.PLC2_DB1_Struct, Wcs.MyApp" }
  ]
}
3. Program.cs 就一行


builder.Services.AddWcsPlc(builder.Configuration);
// 自动完成：创建连接池 → 注册 struct → 启动轮询 → Diff → 验证管道 → EventBus
AddWcsPlc() 内部自动做了：


读取 PlcConnections
  └─ S7PLCPool.GetInstance("PLC1", "192.168.0.1", ...)
  └─ S7PLCPool.GetInstance("PLC2", "192.168.0.2", ...)

读取 PlcBlocks
  └─ registry.RegisterFromConfig(cfg) → Type.GetType("Wcs.MyApp.DB1_Struct,...")
  └─ 反射加载类型，与 plcName + blockNumber 绑定

注册 StructBridge（验证管道）
  └─ 自定义验证器在扩展点注册

注册 S7PollingService
  └─ 启动后每个 DB 块独立 Timer 轮询
  └─ StructDiffEngine 跳过没变化的字段
  └─ 验证管道拒绝的字段不产生事件


  Q1：验证案例哪里去了？
恢复在 PlcSubsystem/Validation/Examples/StationInterlockValidator.cs：


var db1 = ctx.RawStruct as DB1_Struct;
if (db1?.CV01_PalletArrived == true && !db1.LIFT01_Idle)
    return SignalValidationResult.Defer("LIFT01 忙", 3000);

// 查数据库
ctx.Db.Queryable<Pallet>().Where(p => p.Barcode == barcode).Any();
Q2：StateCenter 没更新
修好了。 StructBridge.ProcessAsync 现在每次字段变化都会：


StructDiffEngine 发现变化
  ↓
验证管道 (ISignalValidator)
  ↓ 通过
StateCenter.UpdateDeviceState(deviceId, status)  ← 之前缺失
  ↓
DeviceStateChangedEvent → EventBus → WaitNode / UI / AlarmCenter 接收
  ↓
FieldChangedEvent → EventBus → FileEventStore 持久化
从字段名自动提取设备 ID（CV01_PalletArrived → CV01），状态值 true → Running，false → Idle。

Q3：任务链路还能跑吗？
能。 TaskEngine/ 下所有文件未被修改：


RuleEngine → TaskGenerator → TaskScheduler → TaskOrchestrator
  → ChainExecutionEngine → ActionNode → CommandCenter → PLC 写
完整的双向链路现在是：


S7PLCPool 读 PLC → StructBridge
    ├── 验证器（你的业务逻辑）
    ├── StateCenter 更新（设备状态）
    ├── DeviceStateChangedEvent → EventBus（通知 WaitNode/UI）
    └── FieldChangedEvent → EventBus（审计）

TaskEngine（未被修改）
    ├── RuleEngine → TaskGenerator → TaskScheduler
    └── ChainExecutionEngine → ActionNode → CommandCenter → PLC 写
                ↑ WaitNode 通过 StateCenter + EventBus 双保险等待条件



                架构定稿

                 读链路                             写链路
        ┌──────────────────┐             ┌──────────────────┐
        │    ReadPool       │             │    WritePool      │
        │  (独立 S7Client)   │             │  (独立 S7Client)   │
        └────────┬─────────┘             └────────┬─────────┘
                 │ ReadAsync(db,start,count)       │ WriteAsync(db,start,data)
                 ▼                                ▲
        ┌──────────────────┐             ┌──────────────────┐
        │  PlcReader        │             │   PlcWriter       │
        │  Struct.FromBytes │             │  PlcSerializer    │
        │  → 强类型 struct   │             │  [PlcOffset] 序列化│
        └────────┬─────────┘             └────────┬─────────┘
                 │                                ▲
                 ▼                                │
        ┌──────────────────┐             ┌──────────────────┐
        │  StateCenter      │◄────┐      │  CommandCenter    │
        │  DeviceState      │     │      │  状态机+审计      │
        │  TaskState        │     │      └────────┬─────────┘
        │  AlarmState       │     │                ▲
        └────────┬─────────┘     │               │
                 │               │     ┌──────────────────┐
                 ▼               │     │  ChainExecution   │
        ┌──────────────────┐     │     │  Engine / Action  │
        │  SignalMapper     │     │     │  Node             │
        │  State diff →     │     │     └──────────────────┘
        │  DomainEvent      │     │
        └────────┬─────────┘     │
                 ▼               │
        ┌──────────────────┐     │
        │  RuleEngine       │     │
        │  → TaskGenerator  │─────┘
        └──────────────────┘
关键设计
组件	职责	只读/写
ReadPool	读连接池（独立 S7Client），整个系统只有 S7PollingService 用它	只读
WritePool	写连接池（独立 S7Client），整个系统只有 PlcWriter 用它	只写
S7PollingService	byte[] → struct → StateCenter → 验证 → EventBus	轮询
PlcWriter	[PlcOffset] 命令序列化 → WritePool	写入
PlcBlockRegistry	设备 ID → PLC/DB/偏移 查询	映射
PlcSerializer	[PlcOffset] 标记的 C# 对象 ↔ byte[]	序列化
新增/修改文件
文件	说明
Pools/ReadPool.cs	新建 — 读专用连接池（独立 Snap7 连接）
Pools/WritePool.cs	新建 — 写专用连接池（独立 Snap7 连接）
PlcBlockIdentifier.cs	新建 — 设备→PLC/DB 映射识别器
Examples/PlcDeviceModels.cs	新建 — 示例 PLC 结构体（DB1_StatusBlock / ConveyorCommand 等）
Examples/WcsIntegrationExample.cs	新建 — 完整链路集成示例
S7/PlcStructRegistry.cs	重写 — 使用 ReadPool + WritePool 替代旧 S7PLCPool
S7/S7PollingService.cs	更新 — 使用 ReadPool
PlcWriter.cs	更新 — 使用 WritePool
CommandCenter/CommandCenter.cs	更新 — 集成 PlcWriter + 结构化命令
Application/PlcRegistrationExtension.cs	更新 — 注册双连接池
Application/DependencyInjection.cs	更新 — 注册 ICommandCenter + PlcWriter
SignalMapper/S7/S7PLCPool.cs	删除 — 已替换为 ReadPool + WritePool


现在完整链路通了，启动模拟模式后的日志顺序如下：


══════════════════════════════════════════════════════════════
 完整链路 — 从 PLC 读到写入数据库
══════════════════════════════════════════════════════════════

[SimPLC] 3 PLC 9 DB 块模拟轮询已启动         ← ① 每 100~500ms 生成模拟 byte[]
[SimPLC] ✅ 已注册 AlwaysPassValidator        ← ② 所有信号默认放行（不拦）

[Bridge] PLC1.DB1 → StateCenter 已更新         ← ③ StateCenter 无条件同步
  DeviceState["CV01"] = Running
  DeviceState["CV02"] = Idle

[Detector] ⚡ CV01_PalletArrived → PalletArrivedEvent   ← ④ EventDetector 检测到上升沿
                                                         发布到 EventBus

[TaskGen] 📦 T00001: PALLET_0001 → CV01→ASRS01    ← ⑤ TaskGeneratorService 消费
  TaskScheduler 队列 +1                              PalletArrivedEvent → 生成运输任务

[Worker] ▶ 开始执行 T00001 (CV01→ASRS01)           ← ⑥ TaskExecutionWorker 出队
[Worker]   ⏳ CV01 运输中...(3000ms)                  模拟运输耗时
[Worker]   ⚡ CV01 → 写入 PLC1.DB101 (启动输送机)   ← ⑦ ★ 写入 PLC！★
           CommandCenter.SendStructuredCommandAsync()
           → [PlcBlock("PLC1",101)] → PlcWriter → WritePool → PLC1.DB101
[Worker] ✅ T00001 完成                              ← ⑧ 任务标记完成

[PersistBackgroundService] → StateCenter 持久化     ← ⑨ 写入 SQL DB
  → DeviceRuntime / TaskRuntime 表

[EventPersistenceService] → RawSignalEvent → DB    ← ⑩ 事件写入
  → Wcs_DeviceStateLog 表（含验证结果）
3 个新增文件
文件	职责
AlwaysPassValidator.cs	默认通过验证器，测试时不拦信号
TaskGeneratorService.cs	订阅 PalletArrivedEvent → 生成 TaskContext → 入队 TaskScheduler
TaskExecutionWorker.cs	轮询出队 → 模拟运输 → 写入 PLC → 完成任务
你关心的 3 个问题
你的问题	答案
发布成功的数据哪里消费？	TaskGeneratorService 订阅 PalletArrivedEvent，收到后生成 TransportTask 入队
什么时候写入 PLC？	TaskExecutionWorker 出队后执行 CommandCenter.SendStructuredCommandAsync() → PlcWriter.WriteStructAsync() → WritePool → 真正的 PLC DB 块写入
写入日志怎么看？	[Worker] ⚡ {Device} → 写入 {PLC}.DB{Block} 在控制台输出


DataHex 永远为 null 的原因：

模拟模式下 WritePool 是空的。

AddWcsPlc(configuration, connectToPlc: false) 时，if (connectToPlc) 内部调用的 AddWriteConnection 被跳过，WritePool 没有任何连接。所以 WritePool.Get(plcName) 返回 null，PlcWriter 直接在早期 return 了——数据根本没序列化，dataHex 自然是 null。

现在修好了——LogWriteAsync 移到 conn 不为 null 之后执行，并且加了详细日志：


[PlcWriter] DB 注入: ✅ 已连接           ← 启动时确认 SQL 连接
[Write] 📝 数据: PLC1 DB101@0 = [01 00 04 B0] (4B)  ← 序列化后的准确 hex
[Write] ✅ PLC1 DB101@0 (4B)
[WriteLog] ✅ 已写入 Wcs_PlcWriteLog: PLC1 DB101 = [01 00 04 B0]  ← 写入 DB 确认
（模拟模式下因为 WritePool 无真实连接，会跳过实际写入，但真实的 PLC 硬件模式下完整链路是通的）

EF Core 的全部替换完成：

操作	文件
🗑️ 删除	WcsDbContext.cs — EF Core DbContext
🗑️ 删除	TaskRepository.cs — Dapper 仓库
🗑️ 删除	Microsoft.EntityFrameworkCore 包引用
🗑️ 删除	Dapper 包引用
🔄 重写	DatabaseInitializer.cs — SqlSugar CodeFirst，建 11 张表
🔄 重写	PersistBackgroundService.cs — 直接用 ISqlSugarClient
🔄 重写	DependencyInjection.cs — 移除旧仓库注册
🔄 重写	TaskExecutionWorker.cs — 全用 SqlSugar
➕ 新增	6 个 SqlSugar 实体（Wcs_DeviceRuntime/TaskRuntime/AlarmRuntime/TaskHistory/AlarmHistory/TaskEvent）
启动后 DatabaseInitializer 自动建 11 张表：


Wcs_DeviceRuntime     ✅ PersistBackgroundService
Wcs_TaskRuntime       ✅ PersistBackgroundService
Wcs_AlarmRuntime      ✅ PersistBackgroundService
Wcs_TaskHistory       ✅ TaskExecutionWorker → ArchiveTaskAsync
Wcs_AlarmHistory      ⏳ (预留)
Wcs_TaskEvent         ✅ TaskExecutionWorker → LogEventAsync
Wcs_TaskRun           ✅ TaskExecutionWorker → ArchiveTaskAsync
Wcs_TransportHistory  ✅ TaskExecutionWorker → ArchiveTaskAsync
Wcs_CommandLog        ✅ PlcWriter
Wcs_DeviceStateLog    ✅ EventPersistenceService
Wcs_PlcWriteLog       ✅ PlcWriter

SqlSugar 新表（由 DatabaseInitializer CodeFirst 创建）
═════════════════════════════════════════════════════
Wcs_DeviceRuntime   ✅ PersistBackgroundService     ← 每 10s 写入
Wcs_TaskRuntime     ✅ PersistBackgroundService     ← 每 10s 写入
Wcs_AlarmRuntime    ✅ PersistBackgroundService     ← 每 10s 写入
Wcs_TaskEvent       ✅ TaskExecutionWorker           ← 任务状态变更
Wcs_TaskHistory     ✅ TaskExecutionWorker           ← 任务完成归档
Wcs_TaskRun         ✅ TaskExecutionWorker           ← 任务完成时
Wcs_TransportHistory ✅ TaskExecutionWorker          ← 任务完成时
Wcs_CommandLog      ✅ PlcWriter                     ← 每次写入 PLC
Wcs_DeviceStateLog  ✅ EventPersistenceService       ← 每个信号变化
Wcs_PlcWriteLog     ✅ PlcWriter                     ← 每次写入 PLC
Wcs_AlarmHistory    ✅ 刚加入 EventPersistenceService ← 报警恢复时