# 虚拟 RGV 与区段运动仿真设计说明

## 1. 文档定位

本文定义 WCS Simulation & Verification v1.0 的 S3 虚拟 RGV 与区段运动模型。S3 建立确定性的车辆、区段、位置、载荷和电量仿真能力，为 S4 调度、交通冲突、滚动预留和死锁验证提供可复现的车辆运动底座。

S3 不实现路径搜索、车辆选择、派单、路权抢占、交通协调或死锁解除，也不连接真实 RGV、PLC、网络驱动或设备写入链路。

## 2. 复用基线

S3 复用：

- S0 `SimulationBoundaryGuard`、Manifest、容量和 Evidence 契约；
- S1 `SimulationScenarioEngine`、确定性虚拟时钟、`SimulationStateStore`、Checkpoint、Replay 和 State Hash；
- S2 的会话隔离、Host 只读检查模式和 exact-head 累计回归方法；
- `Wcs.Core.TransportScheduling.TransportVehicleSnapshot`、`TransportVehicleKind.Rgv`、`TransportVehicleOperatingState` 和 `TransportVehicleCapability`。

虚拟车辆状态只投影为统一车辆快照，不写入生产 `ITransportVehicleRegistry`。

## 3. 组件结构

```text
Wcs.Simulator/VirtualRgv
├── VirtualRgvContracts.cs
├── VirtualRgvRuntime.cs
└── VirtualRgvScenarioHandlers.cs
```

Host 只读入口：

```text
Wcs.Host/Controllers/SimulationVirtualRgvController.cs
```

## 4. 配置

配置节：`SimulationVirtualRgv`。

Simulation 默认：

```text
MaximumVehicles = 256
MaximumSegments = 2048
MaximumRouteSegments = 256
MaximumAuditRecords = 5000
MaximumSegmentLengthMillimeters = 10000000
MaximumSpeedMillimetersPerSecond = 20000
BatteryDrainBasisPointsPerMeter = 1
```

SimulationLoadTest 默认：

```text
MaximumVehicles = 2000
MaximumSegments = 20000
MaximumRouteSegments = 2048
MaximumAuditRecords = 50000
MaximumSegmentLengthMillimeters = 100000000
MaximumSpeedMillimetersPerSecond = 50000
BatteryDrainBasisPointsPerMeter = 1
```

所有配置均具有代码硬上限和启动校验，不允许无界车辆、区段、路线或审计集合。

## 5. 区段模型

每个区段包含：

- `SegmentId`；
- `FromNodeId`；
- `ToNodeId`；
- `LengthMillimeters`；
- `SpeedLimitMillimetersPerSecond`；
- `Enabled`。

区段为有向边。路线必须从车辆当前节点开始，并且相邻区段满足前一区段 `ToNodeId` 等于后一区段 `FromNodeId`。禁用区段不能被分配；运行过程中区段变为不可用时，推进操作失败并保留可诊断状态。

## 6. 车辆模型

每辆虚拟 RGV 包含：

- 在线状态和统一运行状态；
- 当前节点或当前区段；
- 区段进度和区段累计运行毫秒；
- 显式区段路线和路线游标；
- 车辆速度；
- 电量基点和电量计算余数；
- 载荷编号；
- 车辆能力；
- 单调版本号；
- 最后虚拟时间偏移。

车辆只允许处于一个节点或一个区段。执行路线时状态为 `Executing`；路线完成后回到终点节点并转为 `Idle`；离线时为 `Offline`。

## 7. 确定性运动

车辆推进由场景虚拟时间决定，不使用 `Task.Delay`、线程睡眠或系统当前时间。

有效速度：

```text
min(车辆速度, 区段速度上限)
```

区段耗时采用整数毫秒向上取整：

```text
ceil(区段长度毫米 × 1000 / 有效速度毫米每秒)
```

一次推进可以跨越多个区段。相同场景版本、Seed、虚拟时间和输入状态必须产生相同位置、完成区段序列、电量和 State Hash。

## 8. 电量模型

电量以 0～10000 基点保存，避免浮点差异。`BatteryDrainBasisPointsPerMeter` 定义每米消耗的基点数，毫米换算余数保存在车辆状态中，确保多次小步推进与一次大步推进得到一致结果。

该电量模型用于仿真可重复性，不代表真实电池化学模型或现场续航标定。

## 9. 载荷模型

`rgv.vehicle.load` 和 `rgv.vehicle.unload` 只允许在线、Idle、位于节点的车辆执行。

- 已有载荷时拒绝重复装载；
- 无载荷时拒绝卸载；
- 可提供 `ExpectedLoadId` 防止卸错载荷；
- S3 不创建生产任务、不修改物料跟踪中心和数据库业务记录。

## 10. 区段占用

区段占用由车辆当前位置派生：车辆 `CurrentSegmentId` 等于区段编号时，该车辆出现在区段占用快照中。

S3 允许测试数据中出现多个车辆同时位于同一区段，用于 S4 冲突检测验证；S3 自身不阻止、不抢占、不释放路权。

## 11. 场景 DSL

动作：

```text
rgv.segment.define
rgv.vehicle.define
rgv.route.assign
rgv.vehicle.advance
rgv.vehicle.online.set
rgv.vehicle.load
rgv.vehicle.unload
```

断言：

```text
rgv.vehicle.at-node
rgv.vehicle.on-segment
rgv.vehicle.state
rgv.vehicle.load.equals
rgv.route.completed
rgv.segment.occupied-by
rgv.vehicle.battery.at-least
```

所有动作和断言都在场景会话的 `SimulationStateStore` 上执行，因此自动进入 Checkpoint、Replay 和 State Hash。

## 12. Host 只读 API

```text
GET /api/simulation/virtual-rgv/runs/{runId}/status
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles/{vehicleId}
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles/{vehicleId}/transport-snapshot
GET /api/simulation/virtual-rgv/runs/{runId}/segments
GET /api/simulation/virtual-rgv/runs/{runId}/segments/{segmentId}
GET /api/simulation/virtual-rgv/runs/{runId}/occupancy
GET /api/simulation/virtual-rgv/runs/{runId}/audit
```

Host 不提供绕过 DSL 的直接车辆移动、路线分配或载荷修改 API。

## 13. 安全边界

- Production 和非批准环境返回 404；
- 无真实 RGV 驱动、PLC、Socket、OPC UA、Modbus 或 S7 连接；
- 无 `UnifiedTransportDispatchEngine`、`ITransportTrafficCoordinator`、`IRouteReservationManager` 或 Deadlock 处理；
- 无车辆选择、派单、任务取消、路权释放或生产数据库写入；
- 仓库 Evidence 不替代 HIL、机械安全和现场验收。

## 14. 后续阶段

S4 在 S3 显式车辆与区段状态之上验证：

- 调度车辆选择；
- 路径与滚动窗口预留；
- 区段冲突和等待关系；
- Wait-For Graph；
- 死锁检测、受控恢复和安全边界。
