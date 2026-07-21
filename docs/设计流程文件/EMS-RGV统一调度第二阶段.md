# EMS / RGV 统一调度第二阶段详细设计

## 1. 阶段目标

第二阶段在第一阶段“车辆选择、完整路径规划、初始资源预留”的基础上，增加真实运行期间需要的执行控制能力：

- 运输任务执行状态机。
- EMS/RGV 位置反馈接入。
- 反馈序号去重与乱序保护。
- 闭塞路段滚动预留。
- 已通过路段分段释放。
- 前方路段冲突时安全等待。
- 装载、卸载确认。
- 暂停、恢复、故障和取消。
- 与厂商协议无关的逻辑命令队列。
- 查询与控制 API。
- Desktop 调度监控页面。

本阶段仍不直接实现具体 PLC 地址写入或 EMS/RGV 厂商报文。协议适配器只需要消费 `TransportExecutionCommand`，并把控制器反馈转换为 `TransportPositionFeedback`。

## 2. 核心状态机

```text
Assigned
  └─ Start
      ├─ MovingToPickup
      │    └─ 到达取货点 -> Loading
      ├─ Loading
      │    └─ ConfirmLoaded -> MovingToDestination
      ├─ MovingToDestination
      │    └─ 到达终点 -> Unloading
      └─ Unloading
           └─ ConfirmUnloaded -> Completed
```

运行中的公共分支：

```text
Moving / Loading / Unloading
  ├─ Pause -> Paused
  ├─ Fault -> Faulted
  ├─ Cancel -> Cancelled
  └─ 前方闭塞冲突 -> WaitingForRoute
```

`Paused` 与 `WaitingForRoute` 可通过 `Resume` 重新尝试前方预留。

## 3. 滚动预留模型

第一阶段派单会计算完整路径，但第二阶段不再一次性锁住整条路线，而是只锁定车辆前方 `ReservationWindowEdges` 条边。

示例：

```text
完整路径: E1 -> E2 -> E3 -> E4 -> E5
窗口大小: 2

车辆在 N1: 预留 E1、E2
车辆到 N2: 释放 E1，保持 E2，补充 E3
车辆到 N3: 释放 E2，保持 E3，补充 E4
车辆到 N4: 释放 E3，保持 E4，补充 E5
```

优点：

- 不会长时间占用整条单线。
- 后车可以在满足安全窗口时逐步进入。
- 冲突只影响前方窗口，不需要重新派单。
- 便于后续加入方向锁、会车区和交叉口控制。

## 4. 位置反馈规则

`TransportPositionFeedback` 包含：

- `VehicleId`
- `NodeId`
- `Sequence`
- `OccurredAtUtc`

规则：

1. `Sequence` 必须单调递增。
2. 重复或乱序反馈直接拒绝。
3. 反馈节点必须位于当前节点之后的剩余路径中。
4. 收到反馈后先释放车辆已通过的路段。
5. 再尝试扩展前方滚动窗口。
6. 扩展失败时进入 `WaitingForRoute`，不生成继续移动命令。

## 5. 逻辑命令队列

执行引擎只生成统一逻辑命令：

- `MoveToNode`
- `Load`
- `Unload`
- `Stop`

协议适配器负责：

```text
TransportExecutionCommand
  -> EMS 控制器任务报文
  -> RGV PLC DB 写入
  -> 厂商 TCP/OPC UA/Modbus 指令
```

因此调度内核不依赖具体通讯协议。

## 6. API

新增 `api/transport`：

```text
GET  /api/transport/vehicles
GET  /api/transport/executions
GET  /api/transport/reservations
POST /api/transport/dispatch
POST /api/transport/executions/{requestId}/start
POST /api/transport/executions/{requestId}/loaded
POST /api/transport/executions/{requestId}/unloaded
POST /api/transport/executions/{requestId}/pause
POST /api/transport/executions/{requestId}/resume
POST /api/transport/executions/{requestId}/fault
POST /api/transport/executions/{requestId}/cancel
POST /api/transport/position-feedback
GET  /api/transport/vehicles/{vehicleId}/commands
```

## 7. 安全原则

- 位置反馈异常时不自动跳过冲突。
- 前方预留失败时车辆必须保持等待。
- 故障状态保留已占路段，避免其他车辆误入。
- 只有完成卸载或明确取消才释放全部资源。
- 真实 PLC 写入仍必须通过 WCS 的唯一写入通道。

## 8. 本阶段边界

暂不包含：

- 数据库持久化和进程重启恢复。
- 多实例分布式预留。
- 单线方向锁。
- 交叉口冲突矩阵。
- 死锁检测和自动解锁。
- 充电任务插单。
- 故障任务自动换车。
- 真实 PLC/EMS/RGV 驱动适配。

这些进入第三阶段。
