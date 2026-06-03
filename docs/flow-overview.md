# WCS Runtime Engine V7 — 完整数据流详解

> 本文档从「PLC 数据采集 → 数据汇聚 → 任务调度 → 链式执行 → 设备控制 → 完成」的全链路视角，解释每个环节的代码位置、模块职责和交互方式。

---

## 目录

1. [项目文件结构](#1-项目文件结构)
2. [五层架构概览](#2-五层架构概览)
3. [完整数据流图](#3-完整数据流图)
4. [Phase 1 — PLC 数据采集](#4-phase-1--plc-数据采集)
5. [Phase 2 — 数据变化检测与事件发布](#5-phase-2--数据变化检测与事件发布)
6. [Phase 3 — 设备状态同步与 StateCenter](#6-phase-3--设备状态同步与-statecenter)
7. [Phase 4 — 任务调度](#7-phase-4--任务调度)
8. [Phase 5 — 任务链执行（DAG）](#8-phase-5--任务链执行dag)
9. [Phase 6 — 设备控制与执行](#9-phase-6--设备控制与执行)
10. [Phase 7 — 任务完成与恢复](#10-phase-7--任务完成与恢复)
11. [横切关注点：EventBus](#11-横切关注点eventbus)
12. [横切关注点：AlarmCenter](#12-横切关注点alarmcenter)
13. [横切关注点：系统恢复RecoveryManager](#13-横切关注点系统恢复recoverymanager)
14. [汇总表：文件 → 职责](#14-汇总表文件--职责)

---

## 1. 项目文件结构

i:\code\IOT\WCS ENG\
├── docs/                                    # 文档
│   ├── step-01-*.md ~ step-08-*.md          # 各 Step 文档
│   ├── step-08-overview.md                  # Step8 总览
│   ├── step-08-phase-0.md ~ phase-4.md      # Step8 各阶段文档
│   ├── step-09-v3-upgrade.md                # V3 升级文档
│   ├── step-10-v4-roadmap.md                # V4 路线图
│   ├── V6-pure-wcs-architecture.md          # V6 纯 WCS 架构定型
│   ├── V7-industrial-observability.md       # V7 工业级可观测性
│   └── flow-overview.md                     # ← 本文档
│
├── src/
│   ├── Wcs.Core/                            # ★ 核心层（所有业务逻辑）
│   │   ├── AlarmCenter/                     #   报警中心
│   │   │   ├── AlarmCenter.cs               #   报警中心入口 + 5层管线
│   │   │   ├── Engine/
│   │   │   │   ├── AlarmAggregationEngine.cs #   聚合引擎（根因树）
│   │   │   │   ├── AlarmDebounceEngine.cs    #   防抖引擎
│   │   │   │   └── AlarmStormGuard.cs        #   风暴抑制引擎
│   │   │   ├── Masking/                     # V3 新增：报警屏蔽
│   │   │   │   ├── AlarmMaskRule.cs          #   屏蔽规则
│   │   │   │   └── AlarmMaskManager.cs       #   屏蔽管理器
│   │   │   ├── Escalation/                  # ★ V5 新增：报警升级
│   │   │   │   ├── AlarmEscalationRule.cs    #   升级规则定义
│   │   │   │   └── AlarmEscalationManager.cs #   升级管理器（Timer驱动）
│   │   │   └── Models/
│   │   │       └── AlarmStateMachine.cs      #   状态机（5状态7转移）
│   │   │
│   │   ├── Common/                          #   公共基类
│   │   │   ├── Interfaces/
│   │   │   │   ├── ISnapshotProvider.cs      #   快照提供者接口
│   │   │   │   └── CommonInterfaces.cs       #   其他公共接口
│   │   │   ├── Models/Result.cs              #   统一返回结果
│   │   │   └── Options/WcsOptions.cs          #   配置选项
│   │   │
│   │   ├── DeviceCenter/                    # ★ 设备管理
│   │   │   ├── Device.cs                     #   设备抽象基类 + IDevice 接口
│   │   │   ├── DeviceManager.cs              #   设备管理器门面（V3: 委托给4子组件）
│   │   │   ├── DeviceRegistry.cs             #   V3 新增：设备注册表
│   │   │   ├── DeviceCommandDispatcher.cs    #   V3 新增：命令调度器
│   │   │   ├── DeviceStateSynchronizer.cs    #   V3 新增：状态同步器
│   │   │   ├── DeviceHealthMonitor.cs        #   V3 新增：健康监控器
│   │   │   ├── Capability/                   # ★ V5 新增：设备能力中心
│   │   │   │   ├── DeviceCapabilityModels.cs
│   │   │   │   └── DeviceCapabilityCenter.cs
│   │   │
│   │   ├── EventBus/                        # ★ 事件总线（系统骨架）
│   │   │   ├── Events/
│   │   │   │   ├── EventBase.cs             #   事件基类
│   │   │   │   └── BusinessEvents.cs         #   业务事件（设备/任务/报警/PLC）
│   │   │   ├── Handlers/IEventHandler.cs     #   事件处理器接口
│   │   │   ├── Persistence/
│   │   │   │   ├── IEventStore.cs            #   事件存储接口
│   │   │   │   ├── FileEventStore.cs         #   文件事件存储（JSON-lines）
│   │   │   │   └── EventReplayService.cs     #   事件重放服务
│   │   │   └── Publisher/
│   │   │       ├── IEventBus.cs              #   事件总线接口
│   │   │       ├── EventBus.cs               #   业务事件总线（DomainBus）
│   │   │       ├── ISignalBus.cs             #   V4 新增：信号总线接口
│   │   │       ├── SignalBus.cs              #   V4 新增：PLC 信号专用通道
│   │   │       └── AlarmBus.cs               #   V7 新增：报警事件专用通道
│   │   │
│   │   ├── ObjectTracking/                  # 物体追踪 + 拓扑
│   │   │   ├── Models/Location.cs            #   位置模型
│   │   │   ├── Models/MovementRecord.cs      #   移动记录
│   │   │   ├── ObjectTrackingCenter.cs       #   追踪中心
│   │   │   └── Topology/                     # ★ 空间拓扑
│   │   │       ├── Zone.cs                   #   区域定义
│   │   │       ├── Node.cs                   #   节点（站点）
│   │   │       ├── Edge.cs                   #   边（路径）
│   │   │       └── TopologyGraph.cs           #   拓扑图（BFS/DFS）
│   │   │
│   │   ├── PlcSubsystem/                    # ★ PLC 子系统
│   │   │   ├── S7Connection.cs               #   S7 连接 + PlcBlock + Crc32Helper
│   │   │   ├── PlcPollingService.cs          #   轮询服务（Timer 驱动, CRC32）
│   │   │   ├── PlcBlockDiffEngine.cs         #   数据块对比引擎（V4: CRC32 预检）
│   │   │   └── SignalMapper/                 #   V3 新增：信号映射层
│   │   │       ├── ISignalMapper.cs
│   │   │       ├── SignalDefinition.cs
│   │   │       └── SignalMapperEngine.cs
│   │   │
│   │   ├── RuleEngine/                       # ★ V4 新增：规则引擎
│   │   │   ├── RuleDefinition.cs              #   规则定义 + 条件 + 动作
│   │   │   ├── IRuleEngine.cs                 #   规则引擎接口
│   │   │   ├── RuleEngine.cs                  #   规则引擎实现
│   │   │   └── TaskGenerator.cs               #   任务生成器
│   │   │
│   │   ├── CommandCenter/                    # ★ V7 新增：命令中心
│   │   │   ├── CommandModels.cs               #   命令状态机（9 状态）
│   │   │   └── CommandCenter.cs               #   命令追踪/超时/审计
│   │   │
│   │   ├── DeadLetterCenter/                 # ★ V7 新增：死信中心
│   │   │   ├── DeadLetterModels.cs            #   死信模型（8 种类型）
│   │   │   └── DeadLetterCenter.cs            #   失败记录管理
│   │   │
│   │   ├── MetricsCenter/                    # ★ V7 新增：指标中心
│   │   │   ├── MetricsModels.cs               #   指标模型（Counter/Gauge/Histogram）
│   │   │   └── MetricsCenter.cs               #   指标收集（内置 9 个默认指标）
│   │   │
│   │   ├── TransportRouteCenter/              # ★ V5 新增：运输路由中心
│   │   │   ├── TransportRouteModels.cs         #   路由模型（请求/结果/拥塞）
│   │   │   └── TransportRouteCenter.cs          #   设备级路径规划（V6: 纯WCS）
│   │   │
│   │   ├── Recovery/                        # ★ 系统恢复
│   │   │   └── RecoveryManager.cs            #   恢复管理器（快照+事件重放）
│   │   │
│   │   ├── ResourceLock/                    # 资源锁
│   │   │   └── ResourceLockManager.cs        #   分布式锁（TTL/Lease/FenceToken/自动清理）
│   │   │
│   │   ├── StateCenter/                     # ★ 状态中心（系统真理源）
│   │   │   ├── Interfaces/IStateCenter.cs    #   状态中心接口
│   │   │   ├── Implementation/              #   V3: 拆分为 5 个独立 Manager
│   │   │   │   ├── StateCenter.cs            #   门面 + 委托
│   │   │   │   ├── DeviceStateManager.cs     #   V3 新增：设备状态管理器
│   │   │   │   ├── TaskStateManager.cs       #   V3 新增：任务运行时管理器
│   │   │   │   ├── AlarmStateManager.cs      #   V3 新增：报警状态管理器
│   │   │   │   ├── ObjectStateManager.cs     #   V3 新增：物体状态管理器
│   │   │   │   └── PlcBlockStateManager.cs   #   V3 新增：PLC 数据块管理器
│   │   │   ├── Models/StateModels.cs         #   状态模型（设备/任务/报警/物体/PLC）
│   │   │   └── Features/
│   │   │       ├── BatchScope.cs             #   批量更新作用域
│   │   │       └── KeyedEventChannel.cs      #   Per-key 订阅通道
│   │   │
│   │   ├── StateMachine/                    # 状态机
│   │   │   ├── IStateMachine.cs              #   状态机接口
│   │   │   ├── DeviceStateMachine.cs         #   设备状态机
│   │   │   └── TaskStateMachine.cs           #   任务状态机
│   │   │
│   │   └── TaskEngine/                      # ★ 任务引擎
│   │       ├── Context/TaskContext.cs         #   任务上下文模型
│   │       ├── Scheduler/
│   │       │   ├── TaskScheduler.cs           #   优先级队列调度器
│   │       │   └── IdempotencyManager.cs      #   幂等性管理器
│   │       ├── Orchestrator/
│   │       │   └── TaskOrchestrator.cs        #   任务编排器
│   │       └── Chain/                        # ★ DAG 链式执行
│   │           ├── TaskNode.cs               #   节点类型（Action/Wait/Delay/Parallel/Decision）
│   │           ├── TaskChainDefinition.cs     #   链版本定义
│   │           ├── ChainBuilder.cs            #   Fluent DAG 构建器
│   │           ├── ChainExecutionEngine.cs    #   DAG 执行引擎
│   │           ├── ChainRecoveryService.cs    #   Chain 断点恢复
│   │           └── TaskChainEngine.cs         #   任务链引擎（串行/并行/DAG）
│   │
│   ├── Wcs.Infrastructure/                  # 基础设施层
│   │   ├── Database/                         #   数据库
│   │   ├── Persistence/                      #   持久化
│   │   ├── PlcAdapter/                       #   PLC 适配器
│   │   ├── S7/                              #   S7 协议实现
│   │   ├── SignalR/                          #   SignalR 实时通信
│   │   └── Logging/                          #   日志
│   │
│   ├── Wcs.Application/                     # 应用层
│   │   ├── DependencyInjection.cs            #   DI 注册
│   │   └── Services/WcsApplicationService.cs #   应用服务
│   │
│   ├── Wcs.Host/                            # 主机层（Web API）
│   │   ├── Program.cs                        #   入口点
│   │   ├── BackgroundServices/               #   后台服务（PLC 轮询启动等）
│   │   ├── Controllers/                      #   API 控制器
│   │   ├── HealthChecks/                     #   健康检查
│   │   └── appsettings.json                 #   配置文件
│   │
│   ├── Wcs.Desktop/                         # 桌面层（Avalonia UI）
│   │   ├── App.axaml.cs                      #   应用入口
│   │   ├── Controls/                         #   自定义控件
│   │   ├── Models/                           #   UI 模型
│   │   └── ViewModels/                       #   视图模型
│   │
│   └── Wcs.Core.Tests/                      # 单元测试（108个）
│       ├── EventBusTests.cs
│       ├── StateCenterTests.cs
│       ├── ResourceLockManagerTests.cs
│       ├── TopologyGraphTests.cs
│       ├── ChainEngineTests.cs
│       └── AlarmCenterTests.cs
```

---

## 2. 五层架构概览

```
┌──────────────────────────────────────────────────┐
│  Desktop Layer  (Wcs.Desktop)                      │  Avalonia UI
│  用户界面、状态面板、报警看板                      │
├──────────────────────────────────────────────────┤
│  Host Layer  (Wcs.Host)                            │  ASP.NET Core
│  Web API、后台服务、健康检查、配置热重载            │
├──────────────────────────────────────────────────┤
│  Application Layer  (Wcs.Application)              │  应用编排
│  DI 注册、应用服务                                 │
├──────────────────────────────────────────────────┤
│  Core Layer  (Wcs.Core)  ★ 核心                    │  业务逻辑
│  PLC采集→事件总线→状态中心→任务引擎→报警           │
├──────────────────────────────────────────────────┤
│  Infrastructure Layer  (Wcs.Infrastructure)        │  基础设施
│  数据库、S7协议、SignalR、持久化                    │
└──────────────────────────────────────────────────┘
```

**核心原则：** 所有业务逻辑在 `Wcs.Core` 中，上层只负责编排和暴露。

---

## 3. 完整数据流图

```
  PLC  ──①──→  PlcPollingService  ──②──→  PlcBlockDiffEngine
                     │                            │
                     │                    ③ 发现数据变化
                     ▼                            │
              PlcBlockChangePublisher              │
                     │                            │
                    ④ 发布 PlcBlockChangedEvent    │
                     │                            │
                     ▼                            │
              ┌─── EventBus ───┐                  │
              │   (事件总线)     │◄─────────────────┘
              └──────┬─────────┘
                     │
             ⑤ 路由到订阅者
                     │
         ┌───────────┼────────────┐
         ▼           ▼            ▼
   StateCenter   TaskScheduler  AlarmCenter
  (状态中心)      (任务调度器)   (报警中心)
         │           │
         │   ⑥ 出队任务          │
         │           │
         │           ▼
         │    TaskOrchestrator
         │      (任务编排)
         │           │
         │   ⑦ ChainExecutionEngine
         │      (DAG 执行)
         │           │
         │   ⑧ 依次执行节点
         │   ┌──────────────┐
         │   │ ActionNode   │ → PlcWrite / HttpCall / Script
         │   │ WaitNode     │ → 等待设备信号或延迟
         │   │ DecisionNode │ → 条件判断（分支/剪枝）
         │   │ ParallelNode │ → 并行执行子分支
         │   │ DelayNode    │ → 定时等待
         │   └──────────────┘
         │           │
         │   ⑨ 设备操作
         │           ▼
         │    DeviceManager → IDevice
         │    (启动/停止/复位)
         │           │
         ▼           ▼
    EventBus ←── 状态变化事件
         │
         ▼
    Desktop UI (SignalR 实时推送)
```

---

## 4. Phase 1 — PLC 数据采集

**涉及文件：**
- `src/Wcs.Core/PlcSubsystem/S7Connection.cs` — S7 连接（连接/读写）
- `src/Wcs.Core/PlcSubsystem/PlcPollingService.cs` — 轮询服务

### 4.1 连接管理

`S7Connection` 实现 `IS7Connection` 接口，提供：

| 方法 | 功能 |
|------|------|
| `ConnectAsync()` | 连接 PLC |
| `ReadBlockAsync(blockNumber, length)` | 读取指定块数据 |
| `WriteBlockAsync(blockNumber, data)` | 写入数据到 PLC |
| `DisconnectAsync()` | 断开连接 |

每个 PLC 连接有独立的配置（`S7ConnectionConfig`）：

```csharp
public class S7ConnectionConfig
{
    string PlcName;    // PLC 名称
    string Address;    // IP 地址
    int Rack, Slot;    // S7 机架/槽位
    int Timeout;       // 超时
    int RetryCount;    // 重试次数
}
```

### 4.2 轮询机制

`PlcPollingService` 使用 `System.Threading.Timer` 驱动定期读取：

```
StartAsync()
  ├── ConnectAllPlcsAsync()      // 连接所有 PLC
  └── StartPollingTimers()       // 为每个 PLC 创建 Timer
        └── PollPlcAsync()       // 定时回调（每 100ms 默认）
              ├── ReadBlockAsync()  // 读取所有启用的块
              └── 缓存到 _lastBlocks
```

- 每个 PLC 可以配置独立的轮询间隔
- 支持断线重连
- 轮询到的块数据缓存到 `_lastBlocks[plcName:blockNumber]`

### 4.3 数据流向

```
PLC (硬件)
  │  ← S7 协议（TCP 102 端口）
  ▼
S7Connection.ReadBlockAsync()
  │  返回 byte[]
  ▼
PlcPollingService.PollPlcAsync()
  │  包装为 PlcBlock { PlcName, BlockNumber, Data, ReadTime }
  ▼
_lastBlocks 缓存
```

---

## 5. Phase 2 — 数据变化检测与事件发布

**涉及文件：**
- `src/Wcs.Core/PlcSubsystem/PlcBlockDiffEngine.cs` — 数据块对比引擎

### 5.1 变化检测

`PlcBlockDiffEngine` 的职责是**对比前后两次读取的 PLC 数据**，找出变化的字节：

```
对比算法：
  for i = 0 to min(len(old), len(new)):
      if old[i] != new[i]:
          recordChange(offset=i, oldValue, newValue)
```

每次调用 `ComparePlcBlocks(oldBlock, newBlock)` 返回 `PlcBlockDiff`：

```csharp
public class PlcBlockDiff
{
    string PlcName;              // 来源 PLC
    int BlockNumber;             // 块号
    byte[] OldData, NewData;     // 前后数据
    List<PlcBlockChange> Changes; // 变化列表（逐字节）
    bool HasChanges;             // 是否有变化
}
```

### 5.2 变化发布

`PlcBlockChangePublisher` 提供订阅-发布模式：

- 调用 `Subscribe(handler)` 注册 `IPlcBlockChangeHandler`
- 调用 `PublishAsync(diff)` 通知所有订阅者

### 5.3 完整的 Phase 1→2 流程

```
PlcPollingService.PollPlcAsync()
  │
  ├── 读取新数据  →  PlcBlock (new)
  │
  ├── PlcBlockDiffEngine.ComparePlcBlocks(lastBlock, newBlock)
  │     └── 发现变化 → PlcBlockDiff
  │
  └── PlcBlockChangePublisher.PublishAsync(diff)
        └── 所有 IPlcBlockChangeHandler 收到通知
              └── 通常：转换为 PlcBlockChangedEvent → EventBus
```

---

## 6. Phase 3 — 设备状态同步与 StateCenter

**涉及文件：**
- `src/Wcs.Core/StateCenter/Implementation/StateCenter.cs` — 状态中心
- `src/Wcs.Core/StateCenter/Models/StateModels.cs` — 状态模型
- `src/Wcs.Core/EventBus/Events/BusinessEvents.cs` — 业务事件

### 6.1 StateCenter 是什么

**StateCenter 是整个系统的「唯一真理源」（Single Source of Truth）**。所有模块的状态数据集中存储在这里，其他模块通过查询 StateCenter 获取最新状态，而不是各自维护副本。

StateCenter 内部维护 5 个 `ConcurrentDictionary`：

```
_deviceStates    <string, DeviceState>    — 设备状态
_taskRuntimes    <string, TaskRuntime>    — 任务运行时状态
_alarmStates     <string, AlarmState>     — 报警状态
_objectStates    <string, ObjectState>    — 物体位置状态
_plcBlockStates  <string, PlcBlockState>  — PLC 数据块状态
```

### 6.2 状态订阅与通知

StateCenter 支持三种通知方式：

| 机制 | 用途 |
|------|------|
| `RegisterListener(IStateChangeListener)` | 传统监听器模式（向后兼容） |
| `WatchDevice(key, handler)` → `IDisposable` | Per-key 订阅（推荐） |
| `_eventBus.PublishAsync()` | 跨模块事件通知 |

**示例：** 当设备状态变化时：

```csharp
StateCenter.UpdateDeviceState("CV_101", newState)
  │
  ├── 对比旧状态，无变化则跳过（Diff 抑制）
  │
  ├── 更新 _deviceStates["CV_101"]
  │
  ├── 通知 IStateChangeListener 列表
  │
  ├── 发布到 KeyedEventChannel (WatchDevice)
  │
  └── EventBus.PublishAsync(DeviceStateChangedEvent)
        └── 其他模块收到通知：
              ├── ChainExecutionEngine.WaitNode → 检查等待条件
              ├── Desktop UI → 更新面板
              └── AlarmCenter → 判断是否需要报警
```

### 6.3 批量更新

`BatchScope` 提供批量更新事务：

```csharp
using (var batch = stateCenter.BeginBatch())
{
    stateCenter.UpdateDeviceState("CV_01", state1);
    stateCenter.UpdateDeviceState("CV_02", state2);
    // batch.Dispose() 时统一通知
}
```

---

## 7. Phase 4 — 任务调度

**涉及文件：**
- `src/Wcs.Core/TaskEngine/Scheduler/TaskScheduler.cs` — 优先级任务调度器
- `src/Wcs.Core/TaskEngine/Context/TaskContext.cs` — 任务上下文

### 7.1 任务模型

`TaskContext` 描述一个待执行的任务：

```csharp
public class TaskContext
{
    string TaskId;              // 唯一 ID
    string DeviceId;            // 目标设备
    int Priority;               // 优先级（越高越优先）
    string RouteId;             // 路由 ID
    TaskStatusEnum Status;      // Created → Running → Completed/Failed
    Dictionary<string, object> Parameters;  // 自定义参数
    int MaxRetries, RetryCount; // 重试策略
    bool IsRetryable;           // 是否可重试
}
```

### 7.2 调度器原理

`TaskScheduler` 使用 `PriorityQueue<TaskContext, int>`，**数值越大优先级越高**：

```
EnqueueAsync(task)
  ├── 缓存到 _taskCache
  ├── 检查设备并发限制（默认 3）
  └── 按优先级入队（Priority 取负值，高优先级先出）

DequeueAsync()
  ├── 循环出队
  ├── 检查设备并发计数 < 限制
  │   ├── 是 → 分配任务，增加设备计数
  │   └── 否 → 放回队列末尾
  └── 返回 TaskContext
```

### 7.3 设备并发控制

每个设备可以设置独立的并发限制（`SetDeviceConcurrencyLimit`），防止同一设备同时执行过多任务。

---

## 8. Phase 5 — 任务链执行（DAG）

**涉及文件：**
- `src/Wcs.Core/TaskEngine/Chain/ChainBuilder.cs` — Fluent DAG 构建
- `src/Wcs.Core/TaskEngine/Chain/ChainExecutionEngine.cs` — DAG 执行引擎
- `src/Wcs.Core/TaskEngine/Chain/TaskNode.cs` — 5 种节点类型
- `src/Wcs.Core/TaskEngine/Chain/ChainRecoveryService.cs` — 断点恢复

### 8.1 DAG 构建

使用 Fluent API 构建有向无环图：

```csharp
var graph = ChainBuilder.Create()
    .AddAction("request", "PlcWrite")          // 请求 PLC 写入
    .AddWait("wait_ready", new WaitCondition    // 等待设备就绪
    {
        DeviceId = "CV01",
        ExpectedStatus = "Ready"
    })
    .DependsOn("wait_ready", "request")        // 等待依赖请求完成
    .AddDecision("check", "CheckStorage",      // 决策：是否入库
        "branch_store", "branch_reject")
    .DependsOn("check", "wait_ready")
    .AddAction("branch_store", "MoveToStorage") // 入库分支
    .DependsOn("branch_store", "check")
    .AddAction("branch_reject", "RejectItem")   // 拒绝分支
    .DependsOn("branch_reject", "check")
    .WithDefinition(new TaskChainDefinition { ... })
    .Build();
```

### 8.2 5 种节点类型

| 节点类型 | class | 执行行为 |
|---------|-------|---------|
| **ActionNode** | 执行动作 | `ExecuteActionNodeAsync()` — 记录日志，返回 true |
| **WaitNode** | 等待条件 | `ExecuteWaitNodeAsync()` — 通过 EventBus 等待设备信号，或定时轮询 |
| **DecisionNode** | 条件分支 | `ExecuteDecisionNodeAsync()` — 调用注册的 handler，缓存结果 |
| **ParallelNode** | 并行执行 | `ExecuteParallelNodeAsync()` — Task.WhenAll 或 WhenAny |
| **DelayNode** | 定时延迟 | `await Task.Delay(DelayMs)` |

### 8.3 DAG 执行引擎

`ChainExecutionEngine.ExecuteAsync(graph)` 核心流程：

```
1. BuildInDegreeMap()        — 计算入度
2. 初始化队列（入度为 0 的节点）
3. while 队列不为空:
   ├── 出队 → ExecuteNodeWithRetryAsync()
   │     ├── 重试循环（MaxRetries=3）
   │     ├── ExecuteNodeAsync() → switch 节点类型
   │     └── 超时 → OperationCanceledException → 重试
   │
   ├── DecisionNode:
   │     ├── 从 _decisionResults 读分支选择
   │     ├── chosenBranch → 入队
   │     └── unchosenBranch → prunedNodes（剪枝）
   │
   ├── 正常节点成功:
   │     ├── CheckpointCompleted() → 持久化断点
   │     └── EnqueueReadySuccessors() → 后继入队
   │
   └── 节点失败:
         └── CheckpointFailed() → 终止执行
4. 返回 TaskGraphResult { Success, CompletedNodes, FailedNodes, SkippedNodes }
```

### 8.4 拓扑排序（Kahn 算法）

`ChainBuilder.Build()` 中使用 Kahn 算法：

```
1. 计算所有节点的入度
2. 入度为 0 的节点入队
3. 出队 → 加入结果列表
4. 减少后继节点的入度
5. 入度变为 0 的后继入队
6. 重复直到队列为空
7. 如果结果数量 ≠ 节点总数 → 说明有环 → 抛出异常
```

### 8.5 Checkpoint 断点恢复

`ChainRecoveryService` 记录每个节点是否已完成：

```
执行前: CheckpointExists? → 已完成的节点直接跳过
执行中: CheckpointCompleted(nodeId) → 记录完成
恢复:   ResumeGraph(graph) → 过滤已完成节点，只执行未完成的
```

---

## 9. Phase 6 — 设备控制与执行

**涉及文件：**
- `src/Wcs.Core/DeviceCenter/DeviceManager.cs` — 设备管理器
- `src/Wcs.Core/DeviceCenter/Device.cs` — 设备抽象基类
- `src/Wcs.Core/DeviceCenter/ConcreteDevices.cs` — 具体设备

### 9.1 设备层级

```
IDevice（接口）
  └── Device（抽象基类）
        ├── ConveyorDevice（输送线）
        ├── RobotDevice（机器人）
        ├── LiftDevice（提升机）
        ├── StackDevice（堆垛机）
        └── SorterDevice（分拣机）
```

### 9.2 设备状态机

设备状态转移图：

```
Idle ──Start──→ Running
Running ──Stop──→ Idle
Running ──Pause──→ Paused
Paused ──Resume──→ Running
Running/Busy ──Error──→ Error
Error ──Reset──→ Idle
Idle ──Maintenance──→ Maintenance
Maintenance ──Reset──→ Idle
```

### 9.3 管理器操作

`DeviceManager` 提供：

- `RegisterDevice(device)` — 注册设备
- `StartDeviceAsync(deviceId)` — 启动设备（发布事件）
- `StopDeviceAsync(deviceId)` — 停止设备（发布事件）
- `SyncDeviceStateAsync(deviceId, newStatus)` — 同步来自 PLC 的状态变化

### 9.4 ActionNode 执行时的设备控制

在真实的 Chain 执行中，`ActionNode` 的 handler 会调用：

```csharp
// 假设注册了 "MoveToStorage" 动作的 handler
engine.RegisterActionHandler("MoveToStorage", async (node, ct) =>
{
    // 1. 获取目标设备
    var device = deviceManager.GetDevice(node.ActionParams["DeviceId"]);

    // 2. 写入 PLC 控制字启动设备
    await s7Connection.WriteBlockAsync(blockNumber, controlData, ct);

    // 3. 更新状态
    stateCenter.UpdateDeviceState(deviceId, new DeviceState { Status = Running });

    // 4. 等待设备完成（通过 WaitNode 或 EventBus）
    return true;
});
```

---

## 10. Phase 7 — 任务完成与恢复

**涉及文件：**
- `src/Wcs.Core/TaskEngine/Orchestrator/TaskOrchestrator.cs` — 任务编排
- `src/Wcs.Core/TaskEngine/Chain/TaskChainEngine.cs` — 任务链引擎
- `src/Wcs.Core/Recovery/RecoveryManager.cs` — 系统恢复

### 10.1 正常完成流程

```
TaskChainEngine.ExecuteChainAsync(chain)
  │
  ├── Serial 模式:
  │      for each task in chain.Tasks:
  │         scheduler.EnqueueAsync(task)
  │         orchestrator.StartTaskAsync(task)
  │         orchestrator.WaitTaskAsync(taskId)
  │            └── TaskCompletionSource 等待完成
  │
  ├── Parallel 模式:
  │      for each task:
  │         scheduler.EnqueueAsync(task)
  │         orchestrator.StartTaskAsync(task)
  │      Task.WhenAll(等待所有任务)
  │
  └── DAG 模式:
         ChainExecutionEngine.ExecuteAsync(graph)
```

### 10.2 TaskOrchestrator 完成处理

```csharp
CompleteTaskAsync(taskId, success)
  ├── 更新 TaskContext（状态、结果、时间）
  ├── StateCenter.UpdateTaskRuntime() → 发布 TaskStateChangedEvent
  ├── scheduler.ReleaseDeviceSlot(deviceId) → 释放并发容量
  └── TaskCompletionSource.SetResult() → 唤醒等待者
```

### 10.3 系统恢复流程

系统崩溃后重启的完整恢复流程：

```
RecoveryManager.RecoverAsync()
  │
  ├── 1. 收集所有 ISnapshotProvider
  │      ├── StateCenter       (RestoreOrder=0)
  │      ├── ObjectTracking     (RestoreOrder=1)
  │      ├── AlarmCenter        (RestoreOrder=2)
  │      └── TaskChain          (RestoreOrder=3)
  │
  ├── 2. OrderBy(RestoreOrder) → 按序恢复
  │      provider.RestoreSnapshotAsync(snapshot)
  │
  ├── 3. 恢复完成后
  │      EventReplayService.ReplayAsync(snapshotTimestamp)
  │        ├── 从 FileEventStore 读取 snapshot 之后的事件
  │        ├── 过滤可重放事件白名单
  │        │   └── DeviceStateChangedEvent
  │        │   └── TaskStateChangedEvent
  │        │   └── ObjectLocationChangedEvent
  │        │   └── PlcBlockChangedEvent
  │        └── 重新发布到 EventBus
  │
  └── 4. ChainRecoveryService 从断点恢复未完成的任务
```

---

## 11. 横切关注点：EventBus

**涉及文件：**
- `src/Wcs.Core/EventBus/Publisher/EventBus.cs` — 事件总线实现
- `src/Wcs.Core/EventBus/Persistence/FileEventStore.cs` — 文件持久化
- `src/Wcs.Core/EventBus/Persistence/EventReplayService.cs` — 事件重放

### 11.1 事件总线角色

EventBus 是整个系统的**骨架**，所有模块通过事件解耦通信：

```
                    ┌──────────────────┐
                    │    EventBus      │
                    │  (内存消息总线)    │
                    └──┬────────────┬─┘
                       │            │
            订阅事件 ◄──┘            └──► 发布事件
              │                                │
              ▼                                ▼
      ChainExecutionEngine               StateCenter
      (WaitNode 等待设备信号)              (状态同步)
              │                                │
              ▼                                ▼
      AlarmCenter                          Desktop UI
      (报警信号处理)                         (实时更新)
```

### 11.2 关键事件类型

| 事件类 | 发布时机 | 订阅者 |
|--------|---------|--------|
| `DeviceStateChangedEvent` | 设备状态变化 | ChainEngine.WaitNode, UI |
| `TaskStateChangedEvent` | 任务状态变化 | TaskOrchestrator, UI |
| `AlarmRaisedEvent` | 报警产生 | AlarmCenter, UI |
| `AlarmRecoveredEvent` | 报警恢复 | AlarmCenter, UI |
| `PlcBlockChangedEvent` | PLC 数据变化 | StateCenter, DeviceManager |
| `ObjectLocationChangedEvent` | 物体移动 | ObjectTracking, UI |

### 11.3 两种订阅方式

// 方式 1：接口处理器（推荐）
class MyHandler : IEventHandler<DeviceStateChangedEvent>
{
    Task HandleAsync(DeviceStateChangedEvent e, CancellationToken ct) { ... }
}
bus.Subscribe(handler);

// 方式 2：委托处理器
bus.Subscribe<DeviceStateChangedEvent>(async (e, ct) =>
{
    // 处理事件
});

## 12. 横切关注点：AlarmCenter

**涉及文件：**
- `src/Wcs.Core/AlarmCenter/AlarmCenter.cs` — 报警中心
- `src/Wcs.Core/AlarmCenter/Engine/AlarmDebounceEngine.cs` — 防抖
- `src/Wcs.Core/AlarmCenter/Engine/AlarmStormGuard.cs` — 风暴抑制
- `src/Wcs.Core/AlarmCenter/Engine/AlarmAggregationEngine.cs` — 聚合根因分析
- `src/Wcs.Core/AlarmCenter/Models/AlarmStateMachine.cs` — 状态机

### 12.1 5 层报警管线
Raw Signal
  │
  ▼
① AlarmDebounceEngine (防抖)
  │   DelayRaise: 信号持续 N 毫秒才确认
  │   DelayRecover: 信号消失 N 毫秒才恢复
  │   避免信号毛刺导致误报
  │
  ▼
② AlarmStormGuard (风暴抑制)
  │   60 秒内 > 1000 次报警 → 进入风暴模式
  │   风暴模式 → 新报警被抑制
  │
  ▼
③ AlarmStateMachine (状态机)
  │   Normal → PendingRaise → Active → Acknowledged
  │   → PendingRecover → Recovered
  │   严格 5 状态 7 转移
  │
  ▼
④ AlarmAggregationEngine (聚合/根因)
  │   相同 Device+Group 的报警归并到根因树
  │   非根因报警自动折叠 Recovery
  │
  ▼
⑤ EventBus (事件发布)
     AlarmRaisedEvent / AlarmRecoveredEvent

### 12.2 报警状态机
        ┌──────────────────────────────────┐
        │                                  │
        ▼                                  │
  Normal ──→ PendingRaise ──→ Active ──→ Acknowledged
        ▲                      │              │
        │                      │   恢复信号     │   恢复信号
        │                      ▼              ▼
        │               PendingRecover ←──────┘
        │                      │
        │                      │  等待延迟到期
        │                      ▼
        └──────────── Recovered

## 13. 横切关注点：系统恢复 RecoveryManager

**涉及文件：**
- `src/Wcs.Core/Recovery/RecoveryManager.cs`

### 13.1 恢复顺序

系统恢复严格按照 `RestoreOrder` 进行：

| Order | 模块 | 原因 |
|-------|------|------|
| 0 | StateCenter | 基础状态，其他模块依赖 |
| 1 | ObjectTrackingCenter | 位置信息，AlarmCenter 需要 |
| 2 | AlarmCenter | 报警状态，需基于设备和位置 |
| 3 | TaskChainEngine | 任务上下文，最后恢复 |

### 13.2 完整恢复流程
RecoveryManager.RecoverAsync()
  │
  ├── Phase 1: 收集快照
  │     每个 ISnapshotProvider.CaptureSnapshotAsync()
  │     → SystemSnapshot { ModuleSnapshots: { "StateCenter": {...}, ... } }
  │
  ├── Phase 2: 保存到持久化存储
  │
  ├── Phase 3: 按 RestoreOrder 恢复
  │     for provider in providers.OrderBy(p => p.RestoreOrder):
  │         provider.RestoreSnapshotAsync(snapshot)
  │
  └── Phase 4: 事件重放
        EventReplayService.ReplayAsync(snapshotTimestamp)
          ├── 读取 FileEventStore 中 snapshot 之后的事件
          ├── 过滤可重放事件（白名单）
          └── 重新发布到 EventBus

## 14. 汇总表：文件 → 职责

| 文件路径 | 职责 | 阶段 |
|---------|------|------|
| `PlcSubsystem/S7Connection.cs` | PLC 连接、块读写 | Phase 1 |
| `PlcSubsystem/PlcPollingService.cs` | 定时轮询采集 PLC 数据 | Phase 1 |
| `PlcSubsystem/PlcBlockDiffEngine.cs` | 数据变化检测 + 变化发布 | Phase 2 |
| `StateCenter/Implementation/StateCenter.cs` | 系统状态中心（真理源） | Phase 3 |
| `StateCenter/Models/StateModels.cs` | 所有状态模型定义 | Phase 3 |
| `EventBus/Publisher/EventBus.cs` | 事件总线（模块解耦） | 贯穿 |
| `EventBus/Events/BusinessEvents.cs` | 业务事件定义 | 贯穿 |
| `TaskEngine/Scheduler/TaskScheduler.cs` | 优先级任务队列 | Phase 4 |
| `TaskEngine/Context/TaskContext.cs` | 任务上下文模型 | Phase 4 |
| `TaskEngine/Chain/ChainBuilder.cs` | DAG Fluent API 构建 | Phase 5 |
| `TaskEngine/Chain/ChainExecutionEngine.cs` | DAG 拓扑排序执行引擎 | Phase 5 |
| `TaskEngine/Chain/TaskNode.cs` | 5 种节点类型定义 | Phase 5 |
| `TaskEngine/Chain/ChainRecoveryService.cs` | DAG 断点恢复 | Phase 5 |
| `TaskEngine/Chain/TaskChainEngine.cs` | 任务链编排（串/并/DAG） | Phase 5 |
| `TaskEngine/Chain/TaskChainDefinition.cs` | 链版本定义 | Phase 5 |
| `TaskEngine/Orchestrator/TaskOrchestrator.cs` | 任务编排（启动/完成/取消） | Phase 7 |
| `DeviceCenter/Device.cs` | 设备基类 + 状态机 | Phase 6 |
| `DeviceCenter/DeviceManager.cs` | 设备注册/启停/事件 | Phase 6 |
| `AlarmCenter/AlarmCenter.cs` | 报警中心 5 层管线 | 贯穿 |
| `AlarmCenter/Engine/AlarmAggregationEngine.cs` | 报警根因树分析 | 贯穿 |
| `ObjectTracking/Topology/TopologyGraph.cs` | 空间拓扑图（BFS 寻路） | 辅助 |
| `ResourceLock/ResourceLockManager.cs` | 分布式资源锁 | 辅助 |
| `Recovery/RecoveryManager.cs` | 系统恢复管理器 | 辅助 |
| `EventBus/Persistence/FileEventStore.cs` | 事件文件持久化 | 辅助 |
| `EventBus/Persistence/EventReplayService.cs` | 事件重放 | 辅助 |

---

## 一句话总结

PLC 发来数据 → PlcPollingService 采到 → PlcBlockDiffEngine 算出变化 →
EventBus 广播通知 → StateCenter 记下状态 → TaskScheduler 排好队 →
ChainExecutionEngine 按 DAG 图一步步执行 → DeviceManager 控制现场设备 →
做完后 TaskOrchestrator 收尾 → 同时 AlarmCenter 全程盯着报警 →
万一崩溃了 RecoveryManager 按 RestoreOrder 逐个恢复再重放事件追回来

---

## 15. V3 架构升级（Step 9）

V3 基于 V2 架构审计发现的 9 个工业级风险点进行整改。详见 [step-09-v3-upgrade.md](step-09-v3-upgrade.md)。

### 新增模块

| 模块 | 文件 | 目的 |
|------|------|------|
| **SignalMapper** | `PlcSubsystem/SignalMapper/` | PLC 地址 → 业务信号事件 |
| **StateManager 拆分** | `StateCenter/Implementation/*Manager.cs` | 5 个独立 Manager 分担 StateCenter |
| **AlarmMask** | `AlarmCenter/Masking/` | 设备维修时动态屏蔽报警 |
| **DeviceRegistry** | `DeviceCenter/DeviceRegistry.cs` | 设备注册专用子组件 |
| **DeviceCommandDispatcher** | `DeviceCenter/DeviceCommandDispatcher.cs` | 设备命令专用子组件 |
| **DeviceStateSynchronizer** | `DeviceCenter/DeviceStateSynchronizer.cs` | 状态同步专用子组件 |
| **DeviceHealthMonitor** | `DeviceCenter/DeviceHealthMonitor.cs` | 健康检查/心跳专用子组件 |
| **BusinessSignals** | `EventBus/Events/BusinessSignals.cs` | 业务信号事件类 |

### 关键升级

| # | 升级 | 详情 |
|---|------|------|
| 1 | **StateCenter 解耦** | 拆为 5 个独立 Manager，各自管理 ConcurrentDictionary + diff + 通知 |
| 2 | **SignalMapper** | `PlcBlockChangedEvent` → 业务信号事件（ConveyorReady/PalletArrived 等） |
| 3 | **DeviceManager 拆分** | 拆为 Registry/CommandDispatcher/StateSynchronizer/HealthMonitor |
| 4 | **WaitNode 双保险** | StateCenter 状态检查 + EventBus 订阅，防止事件丢失 |
| 5 | **FenceToken** | 资源锁单调递增 token，防止 TTL 过期后误操作 |
| 6 | **TaskPriority/Category** | 双维度排序（Category 优先，Priority 次级） |
| 7 | **AlarmMask** | 设备级/报警码级动态报警屏蔽 |
| 8 | **ReservedPosition** | ObjectState 新增 ReservedNodeId/Route，防止双托盘占位 |
| 9 | **EventReplay 白名单** | 移除实时状态事件，只保留有状态恢复需求的事件 |

---

## 16. V4 架构演进（Step 10）

V4 基于 V3 上线后发现的 5 个新风险点进行整改。详见 [step-10-v4-roadmap.md](step-10-v4-roadmap.md)。

### V4 核心升级

| # | 升级 | 文件 | 说明 |
|---|------|------|------|
| 1 | **CRC32 哈希预检** | `PlcBlockDiffEngine` | 先比哈希，不同再逐字节 Diff，大 PLC 性能提升 10-100x |
| 2 | **EventBus 拆分** | `ISignalBus` + `SignalBus` | 信号事件走独立通道，不与业务事件混流 |
| 3 | **ObjectTracking 时间维度** | `ObjectState` + `ObjectTrackingCenter` | LastNode/EnterTime/LeaveTime/TravelTime |
| 4 | **RuleEngine** | `RuleEngine/` 4 个文件 | 业务规则引擎：信号→匹配→生成任务 |
| 5 | **TaskGenerator** | `RuleEngine/TaskGenerator.cs` | 监听 SignalBus，提交流程到调度器 |

### V4 完整数据流

```
PLC → PlcPollingService(CRC32) → PlcBlockDiffEngine(CRC32预检) →
SignalMapper → SignalBus(独立通道) →
    RuleEngine.Evaluate(signalEvent)
      ├── 条件匹配（AND 逻辑）
      ├── ContextKey 分组
      └── 生成 TaskContext
           ↓
    TaskGenerator → TaskScheduler(双维排序) →
    ChainExecutionEngine(State+Event双保险) →
    DeviceManager(FenceToken校验) → IDevice
           ↓
    DomainEventBus(业务事件通道) → 其他模块
```

### 一句话总结 V4

```
CRC32 哈希扛住大 PLC + SignalBus 隔离事件风暴 +
RuleEngine 让业务逻辑脱离 PLC 地址 → 换 PLC 不换逻辑
```

---

## 17. V6 纯 WCS 架构定型

V6 基于 V5 审查，删除所有 WMS 渗透，只保留最纯粹的 WCS 核心。
详见 [V6-pure-wcs-architecture.md](V6-pure-wcs-architecture.md)。

### V6 相比 V5 的净化

| 操作 | 模块 | 原因 |
|------|------|------|
| 🗑️ **删除** | **WorkflowCenter** | 入库/出库/移库流程编排属于 WMS |
| 🔄 **重命名** | **RouteCenter** → **TransportRouteCenter** | 只做设备运输路径，不做库位决策 |
| ✂️ **缩减** | **DeviceCapability** 移除 `CanStore` | 库位决策属于 WMS |
| 🚧 **加边界** | **RuleEngine** 注释禁止 WMS 规则 | 防止误用为订单/库存规则 |

### 纯 WCS 边界（V6 最终版）

```
WCS 只做（保留）                    WMS 不做（删除）
─────────────────────────────      ─────────────────────────────
PLC 通讯、信号采集、信号转换              订单管理
设备注册、启停、状态同步、健康检查         库存管理
设备能力查询（CanLift/CanConvey）        库位管理
设备路径规划、避障、拥塞控制              批次管理
现场实时状态库（StateCenter）            先进先出 FIFO
任务调度与 DAG 链式执行                 库存冻结/盘点
事件总线（模块解耦）                     波次管理
物料追踪（纯运输占位，非库存）             入库/出库策略
设备互斥锁（FenceToken）               库位分配
报警管理（5层管线+屏蔽+升级）            ERP/MES 对接
信号→运输任务映射（RuleEngine）         业务流程编排（WorkflowCenter）
系统崩溃恢复（快照+事件重放）
```

### V6 完整数据流

```
PLC → PlcPollingService(CRC32) → PlcBlockDiffEngine(CRC32预检) →
    SignalMapper → SignalBus(独立通道) →
        RuleEngine(信号→运输任务) → TaskGenerator
        │                                │
        │                    TransportRouteCenter(设备路径/避障)
        │                                ↓
        │                         TaskScheduler(双维排序)
        │                                │
        └── ChainExecutionEngine(State+Event双保险) →
                DeviceManager(FenceToken) → IDevice
                │
         DeviceCapabilityCenter(FindDevices(x=>CanLift))
                │
         ObjectTracking(预占位+时间维度)
                │
         AlarmCenter(5层管线+Mask+Escalation)
                │
         DomainEventBus → StateCenter(5Managers) → Desktop UI
```

---

## 18. V7 工业级可观测性（CommandCenter + DeadLetterCenter + MetricsCenter）

V7 不加新的业务功能，只补生产环境必备的三大基础设施。详见 [V7-industrial-observability.md](V7-industrial-observability.md)。

### V7 新增 3 大模块 + 1 分区

| # | 模块 | 文件 | 价值 |
|---|------|------|------|
| 1 | **CommandCenter** | `CommandCenter/` | 写 PLC ≠ 设备执行，追踪完整生命周期 |
| 2 | **DeadLetterCenter** | `DeadLetterCenter/` | 所有失败集中管理，线上可排查 |
| 3 | **MetricsCenter** | `MetricsCenter/` | 统一指标（Task TPS/PLC延迟/队列深度） |
| 4 | **AlarmBus** | `EventBus/Publisher/AlarmBus.cs` | 报警事件独立通道，3 分区隔离 |

### V7 完整数据流

```
PLC → PlcPollingService(CRC32) → PlcBlockDiffEngine(CRC32预检) →
    SignalMapper → SignalBus(PLC信号通道) →
        RuleEngine(信号→运输任务) → TaskGenerator
        │              │                     │
        │       DeadLetterCenter ← ─ ─ ─ ─ ─ ┘  ← 失败进死信
        │                                │
        │                    TransportRouteCenter(设备路径/避障)
        │                                ↓
        │                         TaskScheduler(双维排序)
        │                                │
        └── ChainExecutionEngine(State+Event双保险) →
                │                          │
          CommandCenter ← ActionNode(写PLC前走CommandCenter)
                │     └─ Sent→Accepted→Executing→Completed
                │     └─ Timeout→DeadLetterCenter
                │
                DeviceManager(FenceToken) → IDevice
                │
         DeviceCapabilityCenter(FindDevices(x=>CanLift))
                │
         ObjectTracking(预占位+时间维度)
                │
         AlarmCenter(5层管线+Mask+Escalation) → AlarmBus(报警通道)
                │
         MetricsCenter(全链路指标收集)
                │
         DomainEventBus → StateCenter(5Managers) → Desktop UI
```

### 全链路可观测性布局

```
CommandCenter → 每条命令：Created→Sent→Accepted→Executing→Completed/Timeout
DeadLetterCenter → 每个失败：Type/Source/Summary/Context/是否已处理
MetricsCenter → 每个指标：task.tps, plc.read_latency_ms, device.active, ...
EventBus分区 → SignalBus(PLC) + DomainBus(业务) + AlarmBus(报警)
```
