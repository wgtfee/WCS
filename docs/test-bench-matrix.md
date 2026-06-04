# WCS Test Bench — 测试矩阵

> V10.1 架构定型后，不再新增模块。接下来全部精力投入稳定性验证。
> 本文档定义完整的测试矩阵，覆盖所有核心链路。

---

## 测试环境

```
Wcs.TestBench/
├── Scenarios/             # 测试场景定义
├── Metrics/               # 指标记录与分析
├── Reports/               # 测试报告
└── Chaos/                 # 故障注入场景
```

**硬件模拟：** Wcs.Simulator（VirtualPlant + 3 PLC 模拟）

---

## 测试 1：基础链路验证

验证 PLC 读到写入的完整闭环是否正确。

| 测试项 | 方法 | 预期 |
|--------|------|------|
| PLC 轮询 | S7PollingService 启动后读到 struct 数据 | StateCenter 5 秒内有更新 |
| StateCenter 同步 | PLC 字段变化后 | StateCenter 能在 200ms 内反映变化 |
| EventDetector 边沿检测 | PLC 字段 false→true | 发布 RawSignalEvent |
| 验证器拦截 | 注入故障信号 | RawSignalEvent.ValidatorPassed=false |
| 领域事件生成 | 正常到位信号 | 发布 PalletArrivedEvent |
| TaskGenerator | 收到 TransportRequestedEvent | TaskScheduler 队列增加 |
| ChainExecutionEngine | 出队任务 | ActionNode 执行 |
| CommandCenter 写入 | ActionNode 触发 | WritePool 收到写入请求 |

---

## 测试 2：10 万次运输任务压力测试

**目标：** 连续运行 10 万次运输任务，统计通过率和性能指标。

```
配置：
  PLC 数:           3
  DB 块数:          6
  轮询间隔:         100ms
  任务生成速率:     5~10 TPS（随机）
  运行时长:         直到 10 万次完成
  设备模拟:         ConveyorSimulator + LiftSimulator + ASRSSimulator

统计指标：
  ┌─────────────────────┬──────────────┐
  │ 指标                │ 预期         │
  ├─────────────────────┼──────────────┤
  │ 任务总数             │ 100,000      │
  │ 成功率               │ ≥ 99.9%      │
  │ 平均任务延迟          │ ≤ 5s         │
  │ 最大任务延迟          │ ≤ 30s        │
  │ 信号检测延迟          │ ≤ 200ms      │
  │ DeadLetter 数        │ ≤ 100        │
  │ 命令超时数            │ ≤ 50         │
  └─────────────────────┴──────────────┘
```

### 运行方式

```csharp
// Program.cs — 虚拟工厂模式
"Simulator": { "Enabled": true, "TransportTps": 5 }

// TestBench 中
var plant = sp.GetRequiredService<VirtualPlant>();
plant.BuildDefaultTopology();
await plant.Generator.StartAsync(ct); // 5 TPS, 直到 10 万次
```

---

## 测试 3：PLC 断线恢复测试

**目标：** PLC 断线后系统自动恢复，任务不丢失。

```
场景 1：短时断线（3 秒）
  步骤：
    1. ReadPool 连接断线
    2. 等待 3 秒
    3. 恢复连接
  预期：
    - StateCenter 保持最后一次有效状态
    - 断线期间生成的任务在恢复后被处理
    - TraceCenter 记录断线事件

场景 2：长时断线（60 秒）
  步骤：
    1. WritePool 连接断线
    2. CommandCenter 发送命令
    3. 60 秒后恢复
  预期：
    - CommandCenter 标记命令为 Timeout
    - DeadLetterCenter 记录超时
    - 恢复后重新建立连接
    - 新一轮轮询正常进行

场景 3：反复断线（每 10 秒断一次，持续 5 分钟）
  预期：
    - 系统不崩溃
    - 内存不增长
    - 日志记录所有断线事件
```

---

## 测试 4：数据库断线恢复

**目标：** SqlSugar 连接断线后验证器降级行为。

```
场景：
  1. 数据库断线
  2. EventDetector 验证器尝试查库
  3. ctx.Db 为 null（降级）
  4. 验证器跳过数据库检查
  5. 数据库恢复
  6. 验证器恢复正常查库

预期：
  - 断线期间 PLC 轮询继续
  - StateCenter 继续同步
  - 验证器跳过数据库检查（不抛异常）
  - 数据库恢复后自动重连
```

---

## 测试 5：内存泄漏检查

**目标：** 72 小时连续运行，内存稳定。

```
方法：
  dotnet counters monitor -n Wcs.Host
  
监控指标：
  GC Heap Size         — 应稳定
  Working Set          — 应稳定
  Gen 0/1/2 Collections — 有规律 GC，不持续增长
  Pause Time           — 单次 ≤ 200ms

预期 72 小时后：
  Memory Usage        — 不超初始值 + 20%
  EventBus 队列深度   — 不持续增长
  TaskScheduler 队列  — 不持续增长
  DeadLetterCenter    — 不超过 1000 条
```

---

## 测试 6：信号风暴测试

**目标：** 短时间内大量 PLC 信号变化，系统不崩溃。

```
场景：
  通过 ChaosMonkey 注入信号风暴：
    100ms 内 100 个噪音信号
    持续 30 秒

预期：
  - EventBus 不丢失事件
  - EventDetector 不抛异常
  - StateCenter 正常更新
  - 验证器正常过滤噪音
  - 系统 CPU 不超过 80%
```

---

## 测试 7：7×24 稳定性测试

**目标：** 连续运行 7 天（168 小时）无故障。

```
配置：
  PLC 数:           3
  DB 块数:          6
  轮询间隔:         100~500ms（随机抖动）
  任务生成:         2 TPS 持续
  故障注入:         5% 概率（设备故障/断线/信号风暴）
  验证器:           7 个工位验证器全部启用

检查点（每 6 小时）：
  ┌─────────────────────┬──────────────┐
  │ 检查项              │ 条件         │
  ├─────────────────────┼──────────────┤
  │ 进程存活            │ 未退出       │
  │ 内存                │ ≤ 1GB        │
  │ CPU 平均            │ ≤ 30%        │
  │ 任务成功率          │ ≥ 99%        │
  │ DeadLetter 数        │ ≤ 500        │
  │ 断线恢复            │ 全部自动恢复  │
  │ 日志无异常           │ 无 Error 级别 │
  └─────────────────────┴──────────────┘
```

---

## 测试 8：快照恢复测试

**目标：** 系统崩溃后恢复，状态不丢失。

```
场景：
  1. 正常运行（50 个任务完成后）
  2. RecoveryManager 创建快照
  3. 强制杀死进程
  4. 重启
  5. RecoveryManager 恢复

预期：
  - TaskScheduler 队列恢复（ITaskQueueStore）
  - StateCenter 恢复到快照时间点
  - EventReplayService 重放快照后事件
  - 正在执行的任务进入 DeadLetter（可人工处理）
```

---

## 测试 9：并发极限测试

**目标：** 最大压力下系统不崩溃。

```
配置：
  任务生成:         50 TPS（持续 10 分钟）
  设备模拟:         20 台设备
  PLC 数:           5
  验证器:           全部启用

统计：
  Maximum TPS Actually Processed
  Average Queue Depth
  Command Timeout Rate
  DeadLetter Rate
  CPU / Memory Peak
```

---

## 测试矩阵汇总

| # | 测试 | 时长 | 通过条件 | 优先级 |
|---|------|------|---------|--------|
| 1 | 基础链路验证 | 10 min | 全链路 100% | P0 |
| 2 | 10 万次压力测试 | 3~6 h | 成功率 ≥ 99.9% | P0 |
| 3 | PLC 断线恢复 | 30 min | 自动恢复不丢任务 | P0 |
| 4 | 数据库断线恢复 | 15 min | 验证器降级不抛异常 | P1 |
| 5 | 内存泄漏检查 | 72 h | 内存不持续增长 | P0 |
| 6 | 信号风暴 | 10 min | CPU ≤ 80% 不丢事件 | P1 |
| 7 | 7×24 稳定性 | 168 h | 全部检查点通过 | P0 |
| 8 | 快照恢复 | 15 min | 恢复后状态一致 | P1 |
| 9 | 并发极限 | 10 min | 系统不崩溃 | P2 |

---

## 测试工具

```csharp
// ChaosMonkey — 故障注入
chaos.FaultProbability = 0.10;
chaos.RegisterDevice(conveyorSim);
chaos.RegisterDevice(liftSim);
await chaos.StartAsync(ct);

// MetricsCenter — 指标收集
metrics.GetValue("task.completed");      // 完成任务数
metrics.GetValue("task.failed");         // 失败任务数
metrics.GetValue("plc.read_latency_ms"); // PLC 读取延迟
metrics.GetSnapshot();                   // 全部指标快照

// DeadLetterCenter — 失败记录查询
deadLetter.GetStats();                   // 统计
deadLetter.Query(type: "CommandTimeout"); // 按类型

// ExecutionHistoryCenter — 运输历史
execHistory.GetPalletHistory("PALLET_0001");
execHistory.GetStats();
```

---

## 一句话总结

```
基础链路(10min) → 10万次压测(6h) → PLC断线(30min) → 
内存泄漏(72h) → 信号风暴(10min) → 7×24稳定性(168h) → 
快照恢复(15min) → 并发极限(10min)

总时长: ~11 天
全部通过 → V10.1 工业可部署
```




① 最大风险其实不是PLC

很多人做WCS最后死在PLC通讯。

实际上大部分项目死在：

任务状态一致性

例如：

CV01 到位
↓
生成 TransportTask
↓
TaskScheduler 入队
↓
ActionNode 发送启动命令
↓
WritePool 成功
↓
PLC 断电
↓
命令没执行

此时：

Task = Executing
PLC = Idle

已经不一致了。

所以我建议增加：

TaskStateMachine

状态：

Created
Queued
Dispatched
CommandSent
Executing
Completed
Failed
Cancelled

而不是简单：

Queued
Running
Done
② EventBus必须支持持久化

你现在：

EventDetector
 ↓
EventBus
 ↓
RuleEngine

如果进程崩了：

PalletArrivedEvent

直接丢失。

工业现场最好：

RawSignalEvent
↓
EventStore
↓
EventBus

例如：

SignalEvents

Id
EventType
Payload
CreatedTime
Consumed

重启后：

EventReplayService

自动补发。

否则：

CV01请求出库
↓
事件丢失
↓
永远没有任务

操作员根本发现不了。

③ RuleEngine不要直接生成任务

这个你Q23已经修正了。

现在是：

RuleEngine
 ↓
TransportRequestedEvent
 ↓
TaskGenerator
 ↓
TaskScheduler

这个非常正确。

因为以后来源会很多：

PLC
WMS
MES
人工按钮
WebAPI

全部统一：

TransportRequestedEvent

即可。

④ EventDetector最好支持组合事件

目前：

CV01_RequestOut

直接：

PalletArrivedEvent

没问题。

但是现场经常：

CV01_RequestOut = true
AND
CV01_BarcodeValid = true
AND
CV01_Fault = false

才允许出库。

建议支持：

{
  "RuleId":"CV01.OutRequest",
  "Conditions":[
      "CV01_RequestOut==true",
      "CV01_BarcodeValid==true",
      "CV01_Fault==false"
  ],
  "Edge":"Rising",
  "TargetEvent":"TransportRequestedEvent"
}

否则后面越来越多验证器。

⑤ SignalSnapshotCenter很重要

这是我认为V10.1新增最正确的模块。

因为：

CurrentStruct
PreviousStruct
Version
LastChanged

实际上：

EventDetector
TraceCenter
DebugCenter

都需要。

统一管理是对的。

⑥ 你的测试矩阵还差一个

我会增加：

测试10：
PLC数据抖动测试

例如：

100ms:
RequestOut = true

200ms:
RequestOut = false

300ms:
RequestOut = true

400ms:
RequestOut = false

连续抖动。

验证：

EventDetector
是否产生重复事件

很多现场光电就是这么玩的。

建议：

DebounceValidator

例如：

200ms内重复变化
忽略

或者：

连续3次轮询保持true
才触发事件
⑦ 我觉得现在唯一还能简化的地方

其实是：

5个StateManager

你之前拆成：

DeviceStateManager
TaskStateManager
AlarmStateManager
ObjectStateManager
PlcBlockStateManager

理论上没问题。

但项目初期我建议：

IStateStore<T>

统一实现。

例如：

StateStore<DeviceState>
StateStore<TaskState>
StateStore<AlarmState>

底层代码能复用80%。

最终评价

如果让我给现在这套架构打分：

项目	评分
PLC通讯层	9.5/10
事件驱动	9/10
任务调度	9/10
可扩展性	10/10
测试体系	10/10
落地可行性	9/10

我认为你现在已经到了：

停止设计
开始编码

的阶段。

后面真正会暴露问题的地方，不会再是架构图，而是：

1. PLC模拟器
2. 10万次压测
3. 72小时内存测试
4. 断线恢复
5. 事件重放

这些跑完以后，架构还能留下来的部分，才是真正需要保留的部分。

从你现在的V10.1来看，我最推荐优先落地的顺序是：

第一周：
S7PollingService
ReadPool
WritePool
PlcSerializer
SignalSnapshotCenter

第二周：
EventDetector
EventBus
RuleEngine
TaskGenerator

第三周：
TaskScheduler
ChainExecutionEngine
CommandCenter

第四周：
VirtualPlant
ChaosMonkey
TestBench

这样一个月内就能跑出第一版可验证系统，而不是继续在架构层面无限迭代。



