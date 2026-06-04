# WCS 架构问答记录

> 本文档记录 WCS Runtime Engine 架构演进过程中的所有关键问题与答案。
> 每个问题记录：问题、回答、决策、原因。
> 每次对话的疑问和解答均在此记录，持续更新。

---

## Q1：StateCenter 过于中心化怎么办？

**问：** StateCenter 同时管理设备状态、任务状态、报警状态、物体状态、PLC 数据块，会不会成为瓶颈？

**答：** 会。拆为 5 个独立 Manager。

**决策：** V3 将 StateCenter 拆为 DeviceStateManager / TaskStateManager / AlarmStateManager / ObjectStateManager / PlcBlockStateManager，各自管理自己的 ConcurrentDictionary + diff + 通知。

**记录时间：** 2024-05

---

## Q2：PLC Diff 粒度太粗，每 100ms 对比整个 byte[] 怎么办？

**问：** 20 台 PLC × 50 块 × 2KB × 100ms = 每秒数百万次字节比较，CPU 受不了。

**答：** 加 CRC32 哈希预检。

**决策：** V4 增加 Crc32Helper，先比较 CRC32 哈希，不同再逐字节 Diff。

**记录时间：** 2024-05

---

## Q3：WaitNode 事件丢失竞态怎么解决？

**问：** Ready 信号先到 EventBus，然后 WaitNode 才开始订阅，永远等不到。

**答：** State + Event 双保险。

**决策：** V3 先查 StateCenter → 状态已满足直接返回 → 不满足再订阅 EventBus。V8 进一步改为 Subscribe-Then-Check（先订阅后检查，根除竞态窗口）。

**记录时间：** 2024-05

---

## Q4：ResourceLock 没有 FenceToken 怎么办？

**问：** TTL 过期后旧持有者继续操作，两个任务同时控制设备。

**答：** 单调递增 FenceToken。

**决策：** V3 引入 Interlocked.Increment 生成 FenceToken，设备操作前 ValidateFenceToken 校验。

**记录时间：** 2024-05

---

## Q5：WritePool 和 ReadPool 要不要分开？

**问：** 读写共用一个 S7PLCPool 会不会有风险？

**答：** 会。写操作被读操作阻塞，命令延迟到达 PLC。

**决策：** V9.5 拆为 ReadPool（独立 S7Client + 读队列）+ WritePool（独立 S7Client + 写队列），写优先级高于读。

**记录时间：** 2024-06

---

## Q6：PLC 信号点位太多，逐条手写 JSON 配置太慢怎么办？

**问：** 现场几千个 PLC 信号，手写 JSON 配置要几天。

**答：** 从博图 TIA Portal 导出的 CSV 批量导入，按命名约定自动推断事件类型。

**决策：** SignalCsvImporter 支持 CSV 批量导入，标签名含 Arrived → PalletArrivedEvent、Fault → DeviceFaultEvent，无需逐条配置。

**记录时间：** 2024-06

---

## Q7：工位验证逻辑太复杂，JSON 配置不够用怎么办？

**问：** 每个工位的验证规则不同，有的查上下游状态、有的查数据库、有的查路径。

**答：** 实现 ISignalValidator 接口，ValidatorContext 提供 StateCenter + Db + RawStruct。

**决策：** V8 引入 ISignalValidator 管道，验证器通过 ValidatorContext 访问所有系统状态，支持 StateCenter 查询、SqlSugar 数据库查询、RawStruct 强类型字段读取。

**记录时间：** 2024-06

---

## Q8：S7PLCPool 的 async void Timer 回调会吞异常怎么办？

**问：** async void 抛异常直接进程崩，请求丢失。

**答：** 改为后台 Task 循环 + SemaphoreSlim 控制并发。

**决策：** V9 重写 S7PLCPool，使用 Channel 队列 + 后台线程单循环处理，写优先，自动重连，重试 2 次。

**记录时间：** 2024-06

---

## Q9：没有真实 PLC 如何进行开发和测试？

**问：** 客户现场 PLC 未到货，开发不能停。

**答：** 创建 VirtualPlant 虚拟工厂。

**决策：** V10 Wcs.Simulator 项目包含 SimulatorSignalSource + ConveyorSimulator + LiftSimulator + ASRSSimulator + TransportGenerator + SignalReplayPlayer + ChaosMonkey。切换真实 PLC 只需改 appsettings.json 一行配置。

**记录时间：** 2024-06

---

## Q10：CommandCenter 缺少 PLC ACK 状态怎么办？

**问：** 写 PLC 成功 ≠ 设备执行完成，需要完整的 ACK/Done 模型。

**答：** 增加 Acked 和 Done 状态。

**决策：** V8 命令状态机改为 Sent → Acked → Executing → Done → Completed。V10 进一步改为 CommandProfile 可配置。

**记录时间：** 2024-06

---

## Q11：PLC 不能改程序，无法加入 GenerationCounter 怎么办？

**问：** 触发模式需要 PLC 维护 2 字节计数器，但现场 PLC 程序不能改。

**答：** 全量轮询 + CRC32 过滤 + StructDiffEngine。

**决策：** V9 S7TriggerPollingService 移除，统一为 S7PollingService，每次读全块，StructDiffEngine 对比 previous，只有变化的字段才触发事件。

**记录时间：** 2024-06

---

## Q12：StateCenter 无限增长怎么办？

**问：** 完成任务、已恢复报警、移动历史数据从来不清理，半年后 StateCenter 内存爆炸。

**答：** StateRetentionPolicy 自动清理。

**决策：** V8 增加 StateRetentionPolicy，完成任务保留 24h、已恢复报警保留 7 天、物体历史最多 1000 条。

**记录时间：** 2024-06

---

## Q13：C# struct 和 PlcSerializer 怎么共存？

**问：** Struct.FromBytes 按字段顺序映射，PlcSerializer 按 PlcOffset 特性映射，哪个用在哪？

**答：** Struct.FromBytes 用于读（byte[] → struct），PlcSerializer 用于写（struct → byte[]）。

**决策：** V9.5 读链路统一用 Struct.FromBytes（按字段顺序填充），写链路统一用 PlcSerializer（按 [PlcOffset] 特性序列化），写 struct 上同时标注 [PlcBlock] 自描述目标 PLC/DB。

**记录时间：** 2024-06

---

## Q14：EventDetector 每次反射整个 struct 性能问题？

**问：** 每次轮询都 GetFields + GetValue 反射，几千个字段性能太差。

**答：** FieldMetadataCache 启动时一次性缓存元数据，运行时零反射。

**决策：** V10.1 增加 FieldMetadataCache，按 struct Type 缓存 FieldMetadata[]，启动时一次反射，运行时从字典直接读取。

**记录时间：** 2024-06

---

## Q15：Validator 拒绝后 StateCenter 要不要更新？

**问：** 验证器拒绝后 StateCenter 不更新，监控面板看到的是假状态。

**答：** StateCenter 永远同步 PLC，验证器只拦截 EventBus 事件。

**决策：** V10.1 S7PollingService 先无条件更新 StateCenter，再走 EventDetector，验证器拒绝不影响 StateCenter。

**记录时间：** 2024-06

---

## Q16：CommandCenter 五态状态机太死板怎么办？

**问：** 西门子现场很多设备没有 Ack/Done 信号，强制五态跑不通。

**答：** CommandProfile 可配置状态机。

**决策：** V10.1 增加 CommandProfile，输送线用 Sent→Executing→Completed（3 态），堆垛机用 Sent→Acked→Executing→Done→Completed（5 态），简单 IO 用 Sent→Completed（2 态）。

**记录时间：** 2024-06

---

## Q17：RuleEngine 收到信号后怎么知道是哪个任务？

**问：** 验证器怎么知道是哪个任务？验证通过后怎么告诉任务？

**答：** 验证器不知道，也不需要知道。

**决策：** V10 明确分层：
- PLC 信号验证器（EventDetector 中）：只验证 PLC 信号合法性，不关心任务 ID
- DAG 验证器（DecisionNode 中）：验证任务执行条件，有任务上下文
- 验证器不直接通知任务，验证器通过 StateCenter + EventBus 间接影响任务，任务通过查询 StateCenter 感知变化

**记录时间：** 2024-06

---

## Q18：Running 状态会触发任务吗？

**问：** 设备 Running=true 持续 10 分钟，每 100ms 生成一次任务？

**答：** 不会。只有边沿变化（false→true）才产生业务事件。

**决策：** V10 EventDetector 只检测上升沿，字段名含 _Arrived / _RequestOut 才生成 PalletArrivedEvent，_Running 不会触发任何任务。

**记录时间：** 2024-06

---

## Q19：RuleEngine 生成的 Task 中谁去验证 PLC 条件？

**问：** 任务生成后，执行前还需要再次确认 PLC 条件是否仍然满足吗？

**答：** 需要。DecisionNode 做任务级验证。

**决策：** V10 区分两类验证：
- 信号验证（EventDetector）：PLC 信号是否合法，过滤噪音信号
- 任务验证（DecisionNode）：任务执行前查 StateCenter 确认条件仍然满足

**记录时间：** 2024-06

---

## Q20：WaitNode 能不能等 StateCenter？

**问：** WaitNode 里 `while(StateCenter.Get("CV01").Status == Running)` 行不行？

**答：** 不行，这是轮询，CPU 空转。

**决策：** V8 Subscribe-Then-Check 方案，WaitNode 先订阅 EventBus 再查 StateCenter，用事件驱动代替轮询，0 CPU 等待。

**记录时间：** 2024-06

---

## Q21：现在架构的 CommandCenter 有 Ack 概念，但现场没 Ack 信号怎么办？

**问：** 西门子 S7-1500 很多设备只有 Busy 和 Done 信号，没有 Ack。

**答：** 可配置命令状态机。

**决策：** V10.1 增加 CommandProfile，每个设备独立定义：
  - `HasAck = false`：跳过 Ack 状态
  - `HasBusy = true`：保留 Executing 状态
  - `HasDone = false`：跳过 Done 状态，直接 Completed

**记录时间：** 2024-06

---

## Q22：多个 PLC 的 Current/Previous struct 放在哪里？

**问：** 以前在 PlcBlockRegistration.PreviousStruct 里，EventDetector 和 TraceCenter 都要用，放在哪？

**答：** SignalSnapshotCenter 统一管理。

**决策：** V10.1 新增 SignalSnapshotCenter，管理所有 PLC 块的 Current/Previous/Version/LastChanged，S7PollingService 轮询后更新、EventDetector 读 previous 做边沿检测、TraceCenter 读快照做审计。

**记录时间：** 2024-06

---

## Q23：RuleEngine 要不要直接生成 Task？

**问：** RuleEngine 收到 PalletArrivedEvent 后直接 CreateTask 行不行？

**答：** 不行。应该发 DomainEvent，由 TaskGenerator 消费。

**决策：** V10.1 RuleEngine → TransportRequestedEvent → TaskGenerator → TaskScheduler。解耦后 WMS/MES/人工/WebAPI 都能发 TransportRequestedEvent 生成任务，不依赖 PLC。

**记录时间：** 2024-06

---

## Q24：架构什么时候停止加模块？

**问：** V1 到 V10.1 一直在加新模块，什么时候停？

**答：** 现在。

**决策：** V10.1 定型。后续不再新增模块，重点转向：
1. PLC 模拟器压测
2. 10 万次运输任务压力测试
3. PLC 断线恢复测试
4. 数据库断线恢复测试
5. 内存泄漏检查
6. 7×24 稳定性测试

**记录时间：** 2024-06

---

## 模块里程碑

| 版本 | 新增模块 | 核心变更 |
|------|---------|---------|
| V1 | 基础结构 | Demo |
| V2 | AlarmCeter, ObjectTracking, TaskChain | Step8 工业增强 |
| V3 | SignalMapper, StateManager, FenceToken, AlarmMask | 架构审计 9 项 |
| V4 | CRC32, SignalBus, RuleEngine, TaskGenerator | 性能+解耦 |
| V5 | RouteCenter, WorkflowCenter, DeviceCapability | WCS 扩展（含 WMS 渗透） |
| V6 | 删除 WorkflowCenter, 纯 WCS 净化 | 边界清理 |
| V7 | CommandCenter, DeadLetterCenter, MetricsCenter, AlarmBus | 可观测性 |
| V8 | ITaskQueueStore, TraceCenter, StateRetention, Subscribe-Then-Check | Production Hardening |
| V9 | ExecutionHistoryCenter | 架构定型 |
| V10 | VirtualPlant, ReadPool, WritePool, PlcBlockAttribute, PlcSerializer | 虚拟工厂+双池 |
| **V10.1** | **EventDetector, SignalSnapshotCenter, FieldMetadataCache, CommandProfile, TransportRequestedEvent** | **最终定型** |

## Q25：SignalSnapshotCenter 和 StateCenter 职责怎么分？

**问：** SignalSnapshotCenter 存 PLC 原始数据，StateCenter 存业务状态，怎么保证不混用？

**答：** 硬性规定访问权限。

**决策：** V10.1 明确分层：
- **SignalSnapshotCenter** = PLC 镜像，只存 `PLC1.DB1.CurrentStruct / PreviousStruct`，只允许 PLC 层（S7PollingService）写入，EventDetector 读取做边沿检测
- **StateCenter** = 业务状态，只存 `DeviceState("CV01").Status`，业务层（RuleEngine/Validator/UI）读取

💀 禁止：`StateCenter.Get<DB1_StatusBlock>()` — 过几个月就分不清哪层是哪层了。

**记录时间：** 2024-06

---

## Q26：EventDetector 为什么要发布 RawSignalEvent？

**问：** 之前直接发布 DomainEvent 不行吗？

**答：** 两级事件管线的目的是可审计、可追踪。

**决策：** V10.1 改为两级事件管线：

```
PLC 字段变化
  ↓
RawSignalEvent（始终发布）
    ├─ PlcName, DbBlock, FieldName
    ├─ OldValue, NewValue, Edge
    └─ ValidatorPassed, ValidatorReason, DomainEventType
  ↓
Validator 管道
  ├─ Pass   → RawSignalEvent.ValidatorPassed=true → DomainEvent → RuleEngine
  └─ Reject → RawSignalEvent.ValidatorPassed=false → 仅记录，不发布 DomainEvent
```

TraceCenter 直接记录 RawSignalEvent，排查问题时一眼看出：

```
09:00:01  PLC1.DB1.CV01_RequestOut  false→true  上升沿  ✅ ValidatorPass
09:00:01  → PalletArrivedEvent published
09:00:01  → TransportRequestedEvent → TaskGenerator
09:00:02  Task T001 Created

09:00:05  PLC1.DB1.CV01_Fault  false→true  上升沿  ❌ Validator Reject("设备维护中")
09:00:05  → PalletArrivedEvent NOT published
```

**记录时间：** 2024-06

---

## 模块里程碑（V10.1 最终版）

| 版本 | 新增模块 | 核心变更 |
|------|---------|---------|
| V1 | 基础结构 | Demo |
| V2 | AlarmCenter, ObjectTracking, TaskChain | Step8 工业增强 |
| V3 | SignalMapper, StateManager, FenceToken, AlarmMask | 架构审计 9 项 |
| V4 | CRC32, SignalBus, RuleEngine, TaskGenerator | 性能+解耦 |
| V5 | RouteCenter, WorkflowCenter, DeviceCapability | WCS 扩展（含 WMS 渗透） |
| V6 | 删除 WorkflowCenter, 纯 WCS 净化 | 边界清理 |
| V7 | CommandCenter, DeadLetterCenter, MetricsCenter, AlarmBus | 可观测性 |
| V8 | ITaskQueueStore, TraceCenter, StateRetention, Subscribe-Then-Check | Production Hardening |
| V9 | ExecutionHistoryCenter | 架构定型 |
| V10 | VirtualPlant, ReadPool, WritePool, PlcBlockAttribute, PlcSerializer | 虚拟工厂+双池 |
| **V10.1** | **EventDetector, SignalSnapshotCenter, FieldMetadataCache, CommandProfile, TransportRequestedEvent, RawSignalEvent** | **最终定型** |

### 最终模块清单（不再增加）

```
Wcs.Core/
├── PlcSubsystem/          PLC 通讯（ReadPool + WritePool + PlcSerializer）
├── SignalSnapshot/        PLC 原始数据快照（Current/Previous/Version）
├── EventDetection/        EventDetector + FieldMetadataCache（两级事件管线）
├── StateCenter/           业务状态（5 Managers，仅存业务状态）
├── EventBus/              事件总线（三分区：SignalBus/DomainBus/AlarmBus）
├── RuleEngine/            规则匹配 + TransportRequestedEvent
├── TaskEngine/            任务调度 + DAG 执行引擎
├── CommandCenter/         命令状态机（CommandProfile 可配置）
├── DeviceCenter/          设备管理
├── ObjectTracking/        物料追踪 + 拓扑
├── AlarmCenter/           报警管理（5 层管线）
├── TransportRouteCenter/  动态路由
├── ResourceLock/          资源锁
├── Recovery/              系统恢复
├── TraceCenter/           执行轨迹
├── ExecutionHistoryCenter/运输历史
├── MetricsCenter/         指标收集
├── DeadLetterCenter/      死信管理
└── Validation/            ISignalValidator 管道
```
