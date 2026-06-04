# WCS Runtime Engine V4 — 架构演进路线图

> 基于 V3 架构审计中发现的 5 个新的工业级风险点的整改方案。
>
> V4 核心主题：**性能 + 解耦 + 业务规则引擎**

---

## 审计风险总览

| # | 风险 | 严重度 | 方案 |
|---|------|--------|------|
| 1 | Polling + Byte-by-byte Diff 撑不住大 PLC | 🔴 | CRC32 哈希预检 + 二级逐字节对比 |
| 2 | EventBus 混合高频信号与业务事件 | 🟡 | 拆为 DomainEventBus + SignalBus |
| 3 | WaitNode 纯 EventBus 竞态（V3 已验证正确） | ✅ | StateCenter + EventBus 双保险（保留） |
| 4 | DeviceManager 未来 100+ 设备仍偏重 | 🟡 | 预留 Device Actor 模式演进路径 |
| 5 | ObjectTracking 缺时间维度 | 🔴 | 增加 LastNode/EnterTime/LeaveTime/TravelTime |

## V4 重点：RuleEngine + TaskGenerator

**这是 V4 最核心的架构升级。** 让业务逻辑彻底脱离 PLC 地址。

### 完整链路

```
PLC DB Block
    ↓ (Poll)
PlcPollingService
    ↓ (Raw byte[])
PlcBlockDiffEngine (CRC32 Hash Pre-check)
    ↓ (PlcBlockDiff)
SignalMapper
    ↓ (Business Signal Event: ConveyorReadyChanged / PalletArrived / DeviceFault)
SignalBus (独立通道，不与业务事件混流)
    ↓
RuleEngine.Evaluate(signalEvent)
    ├── 遍历注册规则
    ├── 检查条件（AND 逻辑）
    ├── 按 ContextKey(如 DeviceId) 分组状态
    └── 全部条件满足 → GenerateTask
         ↓
    TaskGenerator → TaskScheduler.EnqueueAsync
         ↓
    ChainExecutionEngine 执行 DAG
```

### 规则定义示例

```csharp
// 规则："当输送线就绪 + 托盘到位时 → 创建 MoveTask"
RegisterRule(new RuleDefinition
{
    Name = "ConveyorReady → MoveTask",
    ContextKey = "DeviceId",   // 按设备分组跟踪条件状态
    Conditions =
    {
        new RuleCondition
        {
            SignalType = "ConveyorReadyChangedEvent",
            PropertyMatchers = { ["DeviceId"] = "@DeviceId", ["Ready"] = "True" }
        },
        new RuleCondition
        {
            SignalType = "PalletArrivedEvent",
            PropertyMatchers = { ["DeviceId"] = "@DeviceId" }
        }
    },
    Action = new RuleAction
    {
        ActionType = "CreateTask",
        TaskType = "MoveTask",
        Priority = 2,
        DeviceId = "@DeviceId",
        ParameterTemplates = { ["FromNode"] = "@DeviceId", ["ToNode"] = "Storage_Z1" }
    }
});
```

### 规则引擎优势

1. **换 PLC 不换逻辑** — 西门子 → 倍福 → 三菱，只改 SignalMapper 映射，规则不动
2. **业务人员可配置** — 规则以 JSON 存储，无需写代码
3. **按设备隔离** — ContextKey 确保各设备独立跟踪条件状态
4. **可观测** — `GetStats()` 输出命中/生成数

---

## V4 变更清单

### 新增 7 个文件

| 文件 | 模块 | 说明 |
|------|------|------|
| `EventBus/Publisher/ISignalBus.cs` | EventBus | 信号总线接口 |
| `EventBus/Publisher/SignalBus.cs` | EventBus | 信号总线实现 |
| `RuleEngine/RuleDefinition.cs` | RuleEngine | 规则定义模型 |
| `RuleEngine/IRuleEngine.cs` | RuleEngine | 规则引擎接口 |
| `RuleEngine/RuleEngine.cs` | RuleEngine | 规则引擎实现 |
| `RuleEngine/TaskGenerator.cs` | RuleEngine | 任务生成器（SignalBus 订阅者） |

### 修改 5 个文件

| 文件 | 变更 |
|------|------|
| `PlcSubsystem/S7Connection.cs` | PlcBlock 加 `Crc32` 属性；新增 `Crc32Helper` 工具类 |
| `PlcSubsystem/PlcBlockDiffEngine.cs` | `ComparePlcBlocks` 加 CRC32 哈希预检 |
| `PlcSubsystem/PlcPollingService.cs` | 创建 PlcBlock 时计算 CRC32 |
| `StateCenter/Models/StateModels.cs` | ObjectState 加时间维度字段 |
| `ObjectTracking/ObjectTrackingCenter.cs` | MoveObject 更新时间维度 |

### 架构数据流演进

```
V3:
PLC → PlcBlockDiffEngine → SignalMapper → EventBus → 各模块

V4:
PLC → PlcBlockDiffEngine(CRC32) → SignalMapper → SignalBus
                                                       ↓
                                              RuleEngine.Evaluate()
                                                       ↓
                                              TaskGenerator → TaskScheduler
                                                       ↓
                                              EventBus (Domain) → 其他模块
```

### 验证

- `dotnet build` — 0 errors
- `dotnet test` — 全部通过
- `Crc32Helper.Compute()` — 相同数据产生相同哈希，不同数据产生不同哈希
- `RuleEngine.Evaluate()` — 条件匹配正确生成 TaskContext
- `ObjectState` — MoveObject 后 LastNodeId/EnterTime/TravelTimeMs 正确更新

---

## V4+ 规划：Device Actor 模式

当设备数量超过 100+，建议从 DeviceManager 门面模式演进到 Actor 模式：

```
当前: DeviceManager (门面委托)
  ├── DeviceRegistry
  ├── DeviceCommandDispatcher
  ├── DeviceStateSynchronizer
  └── DeviceHealthMonitor

未来: Device Actor (每个设备独立)
  ├── ConveyorActor_01 { State, CommandQueue, EventChannel }
  ├── RobotActor_01 { State, CommandQueue, EventChannel }
  ├── StackerActor_01 { State, CommandQueue, EventChannel }
  └── ActorSystem (Orleans / Akka.NET)
```

每个 Actor 管理自己的状态、命令队列和事件通道，天然支持分布式。
