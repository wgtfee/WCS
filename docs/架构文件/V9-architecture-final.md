# V9: 纯 WCS Runtime Engine — 架构最终定型

> 经过 V1~V9 九次迭代，纯 WCS 架构正式定型。
> 后续方向不再是加新模块，而是压力测试、断线恢复、7×24 稳定性验证。

---

## 最终架构评价

| 维度 | 评分 | 说明 |
|------|------|------|
| PLC 通讯架构 | 9.5/10 | CRC32 哈希预检 + SignalMapper 信号转换 |
| 状态中心 | 9.5/10 | 5 个独立 Manager，per-key 订阅，批量更新，自动清理 |
| 事件总线 | 9/10 | 三分区（SignalBus/DomainBus/AlarmBus） |
| 任务调度 | 9/10 | 双维排序 + 持久化队列 + 设备并发控制 |
| DAG 执行引擎 | 9.5/10 | 5 节点类型 + Checkpoint + Subscribe-Then-Check |
| 设备管理 | 9/10 | 5 子组件门面 + 能力中心 + FenceToken |
| 恢复机制 | 9.5/10 | 快照 + 事件重放 + 持久化队列 + 白名单 |
| 报警系统 | 9.5/10 | 5 层管线 + 屏蔽 + 升级 |
| 可观测性 | 9.5/10 | CommandCenter + MetricsCenter + TraceCenter + DeadLetterCenter |
| 运输追溯 | 9.5/10 | ExecutionHistoryCenter（完整的 Pallet Trace） |
| **总体** | **9.3/10** | **纯 WCS Runtime Platform** |

---

## V9 新增

| # | 模块 | 文件 | 职责 |
|---|------|------|------|
| 1 | **ExecutionHistoryCenter** | `ExecutionHistoryCenter/` | 运输执行历史追溯（Pallet Trace） |

### ExecutionHistoryCenter

记录每个托盘的完整运输轨迹：

```
Pallet P12345
Task T001

Source: RECV_DOCK
Target: ASRS_01
Start:  09:01:00
End:    09:08:00
Total:  7 min

Node Visits:
  09:01:00  RECV_DOCK  arrive    dwell: 30s
  09:01:30  RECV_DOCK  leave
  09:02:00  CV01       arrive    dwell: 20s
  09:02:20  CV01       leave
  09:03:00  LIFT01     arrive    dwell: 2min ← 提升机等待
  09:05:00  LIFT01     leave
  09:06:00  CV08       arrive    dwell: 15s
  09:06:15  CV08       leave
  09:07:00  ASRS_01    arrive    dwell: 1min
  09:08:00  ASRS_01    leave → Completed ✅

统计：
Total Duration: 7 min
Node Count: 5
Wait Time: 3 min (LIFT01 占了大部分)
```

查询方式：
```csharp
// 查某个托盘的所有运输历史
var history = execHistory.GetPalletHistory("PALLET_001");

// 查经过某个设备的所有运输
var cv01Records = execHistory.GetRecordsByNode("CV01");

// 查某次运输的完整节点耗时
var record = execHistory.GetRecord("T001");
foreach (var visit in record.NodeVisits)
    Console.WriteLine($"{visit.NodeId}: {visit.DwellTimeMs}ms");
```

---

## 最终模块清单（V9 完整版）

```
Wcs.Core
│
├─ PlcSubsystem/
│   ├── S7Connection.cs          # PLC 连接 + Crc32Helper
│   ├── PlcPollingService.cs     # 轮询服务 (CRC32)
│   ├── PlcBlockDiffEngine.cs    # CRC32 哈希预检 → 逐字节 Diff
│   └── SignalMapper/            # PLC 地址 → 业务信号事件
│
├─ EventBus/ （三分区）
│   ├── SignalBus                # PLC 信号专用通道（高频）
│   ├── DomainBus                # 业务事件（标准 EventBus）
│   └── AlarmBus                 # 报警事件专用通道（关键）
│
├─ RuleEngine/
│   └── RuleEngine + TaskGenerator   # 信号→运输任务（5s 幂等窗口）
│
├─ TaskEngine/
│   ├── TaskScheduler            # 双维排序 + 持久化队列
│   ├── TaskOrchestrator         # 任务编排
│   └── Chain/                   # DAG 执行引擎
│       ├── ChainBuilder         # Fluent DAG 构建
│       ├── ChainExecutionEngine # 拓扑排序 + Subscribe-Then-Check
│       └── ChainRecoveryService # Checkpoint 断点恢复
│
├─ CommandCenter/                # 命令中心（PLC ACK: Sent→Acked→Executing→Done→Completed）
├─ DeadLetterCenter/             # 死信中心（8 种类型）
├─ MetricsCenter/                # 指标中心（9 个默认指标）
├─ TraceCenter/                  # 执行轨迹中心（Task/Command/Device Trace）
├─ ExecutionHistoryCenter/       # 运输历史追溯（Pallet Trace） ✅ V9 新增
│
├─ DeviceCenter/
│   ├── DeviceManager            # 门面（5 子组件）
│   ├── DeviceCapabilityCenter   # 设备能力（CanLift/CanConvey/...）
│   └── ConcreteDevices          # 输送线/机器人/提升机/堆垛机/分拣机
│
├─ TransportRouteCenter/         # 设备级路径规划 + 避障 + 拥塞控制
├─ StateCenter/                  # 5 个独立 Manager + 自动清理
├─ ObjectTracking/               # 物料追踪（预占位 TTL + 时间维度）
├─ ResourceLock/                 # 资源锁（FenceToken）
├─ AlarmCenter/                  # 5 层管线 + 屏蔽 + 升级
└─ Recovery/                     # 快照 + 事件重放 + 持久化队列恢复
```

---

## 架构演进历程（V1 → V9）

```
V1  Demo 验证
    └── 基础功能可用

V2  Step8 工业级增强
    ├── ResourceLock Lease/TTL
    ├── ObjectTracking 拓扑
    ├── TaskChain 版本管理
    └── AlarmCenter 根因分析

V3  架构审计整改（9项）
    ├── SignalMapper
    ├── StateCenter 解耦（5 Managers）
    ├── DeviceManager 拆分（4 子组件）
    ├── WaitNode 双保险（State+Event）
    ├── FenceToken
    ├── TaskPriority/Category
    ├── AlarmMask
    ├── ReservedPosition
    └── EventReplay 白名单

V4  性能+解耦
    ├── CRC32 哈希预检
    ├── SignalBus 独立通道
    ├── ObjectTracking 时间维度
    └── RuleEngine + TaskGenerator

V5  WCS 扩展（审查发现 WMS 渗透）
    ├── RouteCenter（后改名）
    ├── WorkflowCenter（后删除）
    ├── DeviceCapabilityCenter（后缩减）
    └── AlarmEscalation

V6  纯 WCS 净化
    ├── 删除 WorkflowCenter
    ├── RouteCenter → TransportRouteCenter
    ├── DeviceCapability 移除 CanStore
    └── RuleEngine 加 WMS 边界

V7  工业级可观测性
    ├── CommandCenter（Sent→Executing→Completed）
    ├── DeadLetterCenter
    ├── MetricsCenter
    └── AlarmBus 三分区

V8  Production Hardening
    ├── ITaskQueueStore 持久化队列
    ├── CommandCenter PLC ACK（Acked/Done）
    ├── StateRetentionPolicy 自动清理
    ├── WaitNode Subscribe-Then-Check 无竞态
    ├── Signal 幂等窗口（5s）
    ├── Reservation TTL（5min）
    └── TraceCenter 执行轨迹

V9  架构最终定型 ✅
    ├── ExecutionHistoryCenter 运输追溯
    └── 纯 WCS 架构锁定，不再新增模块
```

---

## V1→V9 代码规模演进

| 版本 | 源文件数 | 特性 |
|------|---------|------|
| V1 | ~20 | 基础 Demo |
| V2 | ~40 | Step8 增强 |
| V3 | ~60 | 架构审计 |
| V4 | ~70 | 性能优化 |
| V5 | ~85 | WCS 扩展（含 WMS 渗透） |
| V6 | ~80 | 纯 WCS 净化 |
| V7 | ~90 | 可观测性 |
| V8 | ~95 | Production Hardening |
| **V9** | **~100** | **架构定型** |

---

## 最终架构评级

```
PLC 通讯层       ⭐⭐⭐⭐⭐   PlcSubsystem + CRC32 + SignalMapper
状态管理层       ⭐⭐⭐⭐⭐   StateCenter(5 Managers) + Retention
事件总线层       ⭐⭐⭐⭐⭐   SignalBus + DomainBus + AlarmBus
任务调度层       ⭐⭐⭐⭐⭐   Scheduler + QueueStore + DualPriority
DAG 执行层       ⭐⭐⭐⭐⭐   5 Node Types + Checkpoint + Subscribe-Then-Check
设备管理层       ⭐⭐⭐⭐⭐   5 Sub-components + Capability + FenceToken
路由规划层       ⭐⭐⭐⭐⭐   TransportRouteCenter + CongestionControl
物料追踪层       ⭐⭐⭐⭐⭐   ObjectTracking + Reservation + TTL + TimeDim
报警管理层       ⭐⭐⭐⭐⭐   5-Layer Pipeline + Mask + Escalation
命令控制层       ⭐⭐⭐⭐⭐   CommandCenter + PLC ACK + Timeout + Audit
可观测性层       ⭐⭐⭐⭐⭐   Metrics + DeadLetter + Trace + ExecutionHistory
恢复机制层       ⭐⭐⭐⭐⭐   Snapshot + EventReplay + QueueStore + Checkpoint
```

---

## 一句话总结 V9

```
V9 = V8 + ExecutionHistoryCenter（运输追溯）
    = 纯 WCS Runtime Engine 架构最终定型
    = 不再新增模块，专注：压力测试 / 断线恢复 / 7×24 稳定性
```
