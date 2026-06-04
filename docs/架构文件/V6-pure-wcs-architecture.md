# V6: 纯 WCS Runtime Engine — 最终架构定型

> 基于 V5 审查后明确删除 WMS 边界，
> 只保留最纯粹的 WCS（设备控制与现场执行）核心能力。

---

## WCS vs WMS 边界定义

```
WCS 只做（保留）                      WMS 不做（删除）
───────────────────────────────    ──────────────────────────────
PLC 通讯、信号采集、信号转换              订单管理
设备注册、启停、状态同步、健康检查         库存管理
设备运输能力查询（可输送/可提升/可转移）   库位管理
设备级路径规划、避障、拥塞控制            批次管理
现场实时状态库（设备/任务/报警/物料）     先进先出 FIFO
任务调度与 DAG 链式执行                库存冻结/盘点
事件总线（模块解耦）                    波次管理
物料追踪（纯运输占位，非库存）            入库/出库策略
设备互斥锁（FenceToken）              库位分配（LocationAllocation）
报警管理（5层管线+屏蔽+升级）            ERP/MES 对接
信号→运输任务映射规则                 业务流程编排（WorkflowCenter）
系统崩溃恢复（快照+事件重放）            存储决策（CanStore/CanAllocate）
```

---

## V6 相比 V5 的变更

| 操作 | 模块 | 原因 |
|------|------|------|
| 🗑️ **删除** | **WorkflowCenter**（4个文件） | 入库/出库/移库流程编排属于 WMS 业务流程 |
| 🔄 **重命名** | **RouteCenter** → **TransportRouteCenter** | 明确只做设备运输路径，不做库位决策 |
| ✂️ **缩减** | **DeviceCapability** 移除 `CanStore`, `CanBuffer` | 库位决策属于 WMS |
| 🚧 **加边界** | **RuleEngine** 注释明确禁止 WMS 规则 | 防止误用为订单/库存/批次规则 |
| ✅ **保留** | **ObjectTracking.ReservedPosition** | 运输资源占位，非库存管理 |

---

## 最终模块清单

### 保留的模块（纯 WCS 核心）

| 模块 | 职责 | 文件数 |
|------|------|--------|
| **PlcSubsystem** | PLC 通讯、信号采集、CRC32 哈希预检 | 6 |
| **SignalBus** | PLC 信号独立通道（防事件风暴） | 2 |
| **EventBus** | 业务事件总线（模块解耦） | 8 |
| **StateCenter** | 现场实时状态库（5 个独立 Manager） | 10 |
| **DeviceCenter** | 设备注册、命令、状态同步、健康检查、能力查询 | 9 |
| **ObjectTracking** | 物料追踪（含预占位+时间维度） | 4 |
| **ResourceLock** | 设备互斥锁（TTL/Lease/FenceToken） | 1 |
| **AlarmCenter** | 报警 5 层管线 + 屏蔽 + 升级 | 8 |
| **TaskEngine** | 任务调度器 + DAG 链式执行 + 编排器 | 9 |
| **TransportRouteCenter** | 设备级路径规划、避障、拥塞控制 | 2 |
| **Recovery** | 系统崩溃恢复（快照+事件重放） | 2 |
| **RuleEngine** | 信号→运输任务映射（禁止 WMS 规则） | 4 |

### 已删除的模块（WMS 渗透）

| 模块 | 文件 | 替代方案 |
|------|------|---------|
| ~~WorkflowCenter~~ | 4 个文件删除 | 业务流程由 WMS 管理，WCS 只执行具体运输任务 |

---

## 最终数据流

```
PLC → PlcPollingService(CRC32)
         ↓
    PlcBlockDiffEngine(CRC32预检) ─── 无变化 → 跳过
         ↓ 有变化
    SignalMapper(PLC地址→业务信号)
         ↓
    SignalBus(独立信号通道)
         ↓
    ┌── RuleEngine(仅信号→运输任务) ──→ TaskGenerator
    │                                         ↓
    │                                   TransportRouteCenter(设备路径)
    │                                         ↓
    │                                   TaskScheduler(双维排序)
    │                                         ↓
    ├── ChainExecutionEngine(State+Event双保险)
    │         ↓
    │    WaitNode(先查StateCenter→再订阅EventBus)
    │    DecisionNode(条件分支)
    │    ActionNode(设备操作)
    │    DelayNode(延迟等待)
    │         ↓
    ├── DeviceManager(FenceToken校验)
    │         ↓
    │    DeviceCapabilityCenter(FindDevices(x=>CanLift))
    │         ↓
    ├── AlarmCenter(5层管线+Mask+Escalation)
    │         ↓
    └── ObjectTracking(预占位+时间维度)
              ↓
    EventBus(Domain) → StateCenter(5Managers) → Desktop UI
```

---

## V1~V6 演进历程

```
V1   Demo 验证
V2   Step8 工业级增强（5 项）
V3   9 项架构审计整改（SignalMapper+StateCenter解耦+FenceToken+...）
V4   性能+解耦+RuleEngine（CRC32+SignalBus+RuleEngine+TaskGenerator）
V5   WCS 内核扩展（RouteCenter+WorkflowCenter+DeviceCapability+AlarmEscalation）
     └── 审查发现 WMS 渗透
V6   纯 WCS 净化
     ├── 删除 WorkflowCenter（业务流程→WMS）
     ├── RouteCenter → TransportRouteCenter（设备路径）
     ├── DeviceCapability 移除 CanStore（库位决策→WMS）
     └── RuleEngine 加 WMS 防护边界
```

---

## 一句话总结

```
V6 = V5 − WorkflowCenter − CanStore − RouteCenter(重命名为TransportRouteCenter)
     + WMS 防护边界
     = 纯 WCS Runtime Engine
```
