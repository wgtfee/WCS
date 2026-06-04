# WCS 闭环架构详解 — 信号→任务→验证→反馈

> 回答：PLC 请求从哪进入任务流程？验证器怎么对应任务？
> 验证通过怎么告诉任务？任务结束后什么时候写入？
>
> 每次对话的问题和答案记录在此文档中。

---

## 你的 5 个疑问

| # | 疑问 | 回答位置 |
|---|------|---------|
| 1 | PLC 请求到哪里解析放进任务流程？ | 见 §2 |
| 2 | 验证器怎么对应相应的任务？ | 见 §3 |
| 3 | 验证器完成之后怎么告诉对应的任务？ | 见 §4 |
| 4 | 任务结束后什么时候去写入？ | 见 §5 |
| 5 | 整体闭环链路是什么样的？ | 见 §6 |

---

## §1 当前架构回顾

目前你的架构是这样的：

```
S7PollingService (Timer 轮询)
  │ 读 PLC → byte[] → struct → StateCenter
  │ 验证管道 (ISignalValidator)
  │ EventBus 发布 DeviceStateChangedEvent
  ▼
StateCenter (设备状态)
  │
  ▼ (其他模块从这里读状态)
  RuleEngine → TaskGenerator → TaskScheduler → ChainExecutionEngine
                                                       │
                                                  ActionNode → CommandCenter → WritePool → PLC
```

**但这里有一个断裂：** `S7PollingService` 里的验证器只负责"这个 PLC 信号能不能通过"，它不负责"生成任务"。验证通过后信号进了 StateCenter，但**没有自动进入 RuleEngine**。

---

## §2 PLC 请求从哪进入任务流程？

### 正确流程

```
Timer 读到 PLC 数据变化
  ↓
StructDiffEngine 发现字段变化（如 CV01_PalletArrived: false → true）
  ↓
StateCenter.UpdateDeviceState("CV01", Running)
  ↓
EventBus.PublishAsync(DeviceStateChangedEvent { DeviceId = "CV01" })
  ↓
┌───────────────────────────────────────────────────┐
│           这里才是进入任务流程的入口                  │
│                                                   │
│ RuleEngine 订阅 DeviceStateChangedEvent             │
│   规则: IF 设备=CV01 AND 状态=Running              │
│         AND StateCenter("LIFT01").Idle             │
│         AND 无验证器拒绝                            │
│   动作: Create TransportTask(CV01 → LIFT01)        │
│         → TaskScheduler.Enqueue(task)              │
│                                                   │
│ ChainExecutionEngine.Dequeue() → 执行 DAG 图        │
└───────────────────────────────────────────────────┘
```

**关键：** `RuleEngine` 才是"PLC 请求→任务"的转换器。`RuleEngine` 里可以配置规则，例如：

```
Rule "托盘到位触发运输":
  触发条件: DeviceStateChangedEvent.DeviceId == "CV01"
          AND DeviceStateChangedEvent.NewStatus == Running
  前置检查: StateCenter.GetDeviceState("LIFT01").Status == Idle
  前置检查: StateCenter.GetDeviceState("ASRS01").Status == Idle
  动作: 生成 TransportTask { From=CV01, To=ASRS01 }
```

---

## §3 验证器怎么对应任务？

**验证器有两种，职责不同，不能混用：**

### 类型 1：PLC 信号验证器（在 S7PollingService 中运行）
- **职责：** 验证 PLC 信号本身是否合法（防噪音、防毛刺、防误报）
- **运行时机：** 每次 Timer 轮询到数据变化时
- **输入：** `ValidatorContext`（当前 struct、上一次 struct、StateCenter、数据库）
- **输出：** Pass / Reject / Defer
- **例子：** "CV01 故障了所以这个到达信号无效"、"LIFT01 维护中不接收新请求"
- **注册位置：** `S7PollingService.RegisterValidator()`

### 类型 2：任务执行验证器（在 ChainExecutionEngine 中运行）
- **职责：** 验证 DAG 图中某个 ActionNode / DecisionNode 的执行条件
- **运行时机：** DAG 图执行到该节点时
- **输入：** 当前 StateCenter 状态 + 任务上下文
- **输出：** true/false（决定分支走哪条）
- **例子：** "执行 MoveToLift 前检查 LIFT01 是否真的空闲"
- **注册位置：** `engine.RegisterDecisionHandler()`

### 当前架构中的对应

| 你的疑问 | 对应的验证器类型 | 位置 |
|---------|----------------|------|
| CV01 到位信号是否有效 | 类型 1 — PLC 信号验证器 | `S7PollingService` |
| CV01→LIFT01 是否可以运输 | 类型 2 — 任务执行验证器 | `ChainExecutionEngine.DecisionNode` |
| 运输途中设备是否故障 | 类型 2 — 任务执行验证器 | `ChainExecutionEngine.WaitNode`（通过 StateCenter 查询） |
| 任务完成后写入哪个 DB 块 | 不涉及验证 | `ActionNode` → `CommandCenter` |

---

## §4 验证通过后怎么告诉对应的任务？

### 验证器不直接"告诉"任务

验证器是**无状态的过滤器**，它不持有任务引用，也不直接通知任务。

任务通过**查询 StateCenter** 来感知验证结果：

```
Timer 读到 PLC 数据
  ↓
S7PollingService 验证管道（类型 1）
  ├─ 拒绝 → 不更新 StateCenter，不发布事件
  └─ 通过 → StateCenter.UpdateDeviceState() → EventBus 发布
                ↓
           RuleEngine 收到事件
                ↓
           IF 规则匹配 → 生成任务 → TaskScheduler
                ↓
           ChainExecutionEngine 出队任务 → 执行 DAG
                │
           ┌──── DecisionNode: "检查 LIFT01 是否空闲"
           │     → StateCenter.GetDeviceState("LIFT01")  ← 这里读到验证结果
           │     → IF Idle → 继续执行
           │     → IF Busy  → 走重试/等待分支
           │
           └──── WaitNode: "等待 CV01 到位"
                  → StateCenter.GetDeviceState("CV01") == Running
                  → 或订阅 EventBus 等待 DeviceStateChangedEvent
```

**所以：**
- 验证器不直接告诉任务
- 验证器通过 **StateCenter 的状态** 和 **EventBus 的事件** 间接影响任务
- 任务通过 **查询 StateCenter** 和 **订阅 EventBus** 来感知验证结果

---

## §5 任务结束后什么时候写入？

### DAG 执行完成 → ActionNode 触发写入

```
ChainExecutionEngine 按 DAG 图执行
  │
  ├── ActionNode: "启动 CV01"
  │     → CommandCenter.SendStructuredCommandAsync("CV01", "Start",
  │         new ConveyorCommand { Start = true, Speed = 1500 })
  │     → 内部自动: [PlcBlock("PLC1",101)] → WritePool("PLC1") → DB101
  │     → 这里的写入是在 DAG 执行到这个节点时触发的
  │
  ├── WaitNode: "等待 CV01 到位"
  │     → 查询 StateCenter 或订阅 EventBus
  │     → S7PollingService 下一轮读到 PLC 反馈后更新 StateCenter
  │     → WaitNode 条件满足 → 继续执行
  │
  ├── ActionNode: "提升机上升"
  │     → CommandCenter.SendStructuredCommandAsync("LIFT01", "LiftUp",
  │         new LiftCommand { GoUp = true, TargetFloor = 2 })
  │     → [PlcBlock("PLC1",102)] → WritePool("PLC1") → DB102
  │
  ├── WaitNode: "等待 LIFT01 到位"
  │
  └── ActionNode: "堆垛机入库"
        → CommandCenter.SendStructuredCommandAsync("ASRS01", "Store",
            new AsrsStoreCommand { StartStore = true, Column = 15, Row = 8 })
        → [PlcBlock("PLC2",201)] → WritePool("PLC2") → DB201
```

**写入时机不是在"任务完成后"，而是在"DAG 执行到某个 ActionNode 时"。**
一个 DAG 图可能在执行过程中多次写入不同 PLC：

```
Task: 从 CV01 运输到 ASRS01
  DAG 图:
    Action: 启动 CV01 (写 PLC1.DB101)    ← 第 1 次写入
    Wait:   CV01 到位 (读 PLC1.DB1)
    Action: 启动 LIFT01 (写 PLC1.DB102)  ← 第 2 次写入
    Wait:   LIFT01 到位 (读 PLC1.DB1)
    Action: 入库 ASRS01 (写 PLC2.DB201)  ← 第 3 次写入
    Wait:   ASRS01 完成 (读 PLC1.DB2)
```

---

## §6 完整闭环链路图

```


┌─────────────────────────────────────────────────────────────────────────┐
│                        完整信号→任务→写入→反馈 闭环                        │
└─────────────────────────────────────────────────────────────────────────┘

  ① 轮询 (100ms)
  ReadPool("PLC1").ReadAsync(DB1, 0, 6)
    → byte[6]
    → Struct.FromBytes<DB1_StatusBlock>(bytes)
    → DB1_StatusBlock.CV01_PalletArrived = true
    │
    ├─ ② PLC 信号验证器 (S7PollingService)
    │     Cv01_ArrivalValidator.Validate(ctx)
    │       ├─ Reject → 不更新 StateCenter，信号丢弃
    │       └─ Pass   → 继续
    │
    ├─ ③ StateCenter 更新
    │     UpdateDeviceState("CV01", Running)
    │     EventBus.PublishAsync(DeviceStateChangedEvent { DeviceId = "CV01" })
    │
    ├─ ④ RuleEngine (订阅 EventBus)
    │     规则: IF CV01=Running AND LIFT01=Idle AND ASRS01=Idle
    │     THEN CreateTask(From=CV01, To=ASRS01, Priority=Normal)
    │     → TaskScheduler.Enqueue(task)
    │
    ├─ ⑤ ChainExecutionEngine 出队执行 DAG
    │     │
    │     │   DecisionNode: "验证运输条件" ← 类型 2 验证器
    │     │     → StateCenter.GetDeviceState("LIFT01")
    │     │     → 通过则继续，否则走重试
    │     │
    │     ├── ActionNode: "启动 CV01" ← 第 1 次写入
    │     │     → CommandCenter.SendStructuredCommandAsync("CV01", "Start",
    │     │         new ConveyorCommand { Start = true })
    │     │     → [PlcBlock("PLC1",101)] → PlcWriter → WritePool("PLC1") → DB101
    │     │
    │     ├── WaitNode: "等待 CV01 到位"
    │     │     → StateCenter.GetDeviceState("CV01") == Running
    │     │     → 或等待 EventBus 事件
    │     │     → ② 下一轮轮询会读到 PLC 反馈，更新 StateCenter
    │     │
    │     ├── ActionNode: "启动 LIFT01" ← 第 2 次写入
    │     │     → CommandCenter.SendStructuredCommandAsync("LIFT01", "LiftUp",
    │     │         new LiftCommand { GoUp = true })
    │     │     → [PlcBlock("PLC1",102)] → WritePool("PLC1") → DB102
    │     │
    │     ├── WaitNode: "等待 LIFT01 到位"
    │     │
    │     ├── ActionNode: "入库 ASRS01" ← 第 3 次写入
    │     │     → CommandCenter.SendStructuredCommandAsync("ASRS01", "Store",
    │     │         new AsrsStoreCommand { StartStore = true, Column = 15, Row = 8 })
    │     │     → [PlcBlock("PLC2",201)] → WritePool("PLC2") → DB201
    │     │
    │     └── WaitNode: "等待 ASRS01 完成"
    │
    └─ ⑥ ExecutionHistoryCenter
          Pallet: PALLET_0001
          Route: CV01 → LIFT01 → ASRS01
          Nodes: { CV01(3s), LIFT01(5s), ASRS01(8s) }
          Status: Completed


┌─────────────────────────────────────────────────────────────────────────┐
│                      三个关键原则                                         │
└─────────────────────────────────────────────────────────────────────────┘

原则 1：验证器不直接通知任务
  验证器 → StateCenter + EventBus → 任务查询 ← 这是正确的解耦方式

原则 2：写入发生在 ActionNode，不是任务完成后
  DAG 图中的每个 ActionNode 都可能触发一次写入
  一个 DAG 图可以多次写入不同 PLC 的不同 DB 块

原则 3：轮询 + StateCenter 是闭环节点
  写入 → PLC 执行 → 轮询读到变化 → StateCenter 更新 → WaitNode 满足 → 下一步
  这个"写入→反馈"环完全靠 S7PollingService 的 Timer 轮询闭合
```

---

## §7 常见疑问解答（持续更新）

### Q: 验证器拒绝后会怎么样？
A: 信号被丢弃，StateCenter 不更新，EventBus 不发布，RuleEngine 收不到事件，不会生成任务。PLC 侧信号仍然保持，下一次轮询会再次触发验证。

### Q: WaitNode 等不到条件满足怎么办？
A: 有两种超时机制：
   1. WaitNode 本身的 TimeoutMs（30 秒默认）→ 超时后 DAG 执行失败，走重试逻辑
   2. 最外层 DAG 图的整体超时 → 超时后整条链失败

### Q: 同一个设备同时有两个任务在等待怎么办？
A: TaskScheduler 的 `DeviceConcurrencyLimit` 控制每个设备的并发数（默认 3）。
   超过限制的任务在队列中等待，不会出队。

### Q: 写入 PLC 后怎么确认它执行了？
A: 写入后下一轮轮询会读到 PLC 的反馈信号。
   例如写入 StartConveyor 后，轮询读到 CV01_PalletArrived = true 才算确认。
   CommandCenter 的状态机（Sent→Acked→Executing→Done→Completed）跟踪这个确认过程。

### Q: 类型 1（信号验证器）和类型 2（任务验证器）能不能合并？
A: 不能混用。类型 1 在轮询线程中运行，必须轻量快速，只做信号级判断。
   类型 2 在 DAG 执行线程中运行，可以访问完整任务上下文，做业务级判断。
   混用会导致轮询线程阻塞，影响其他设备的响应时间。
