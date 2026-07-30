# 虚拟 RGV 场景开发与调试操作手册

## 1. 适用范围

本文用于在 `Simulation` 或 `SimulationLoadTest` 环境中编写和调试 S3 虚拟 RGV/区段场景。Production 和非批准环境不可使用这些 API。

S3 只模拟显式车辆运动，不自动规划路线、选择车辆、派单、申请路权或处理死锁。

## 2. 启动配置

必须同时满足：

```text
Simulator.Enabled=true
SimulationGovernance.Enabled=true
Environment=Simulation 或 SimulationLoadTest
```

配置节：

```json
{
  "SimulationVirtualRgv": {
    "MaximumVehicles": 256,
    "MaximumSegments": 2048,
    "MaximumRouteSegments": 256,
    "MaximumAuditRecords": 5000,
    "MaximumSegmentLengthMillimeters": 10000000,
    "MaximumSpeedMillimetersPerSecond": 20000,
    "BatteryDrainBasisPointsPerMeter": 1
  }
}
```

Production 必须保持仿真关闭。仿真运行前还应确认异常检测、ML、Forecast 和生产遥测写入保持禁用。

## 3. 推荐场景顺序

```text
定义区段
→ 定义车辆
→ 可选装载
→ 分配显式区段路线
→ 在目标虚拟时间执行 advance
→ 使用断言验证节点、区段、状态、载荷、电量与占用
→ 创建 Checkpoint
→ Replay 或恢复后继续运行
```

## 4. 定义区段

```json
{
  "Id": "define-s1",
  "AtMilliseconds": 0,
  "Order": 0,
  "Kind": "rgv.segment.define",
  "Target": "S1",
  "Payload": {
    "FromNodeId": "N1",
    "ToNodeId": "N2",
    "LengthMillimeters": 1000,
    "SpeedLimitMillimetersPerSecond": 1000,
    "Enabled": true
  }
}
```

区段是有向的。反向运动需要单独定义反向区段。

## 5. 定义车辆

```json
{
  "Id": "define-rgv-01",
  "AtMilliseconds": 0,
  "Order": 10,
  "Kind": "rgv.vehicle.define",
  "Target": "RGV-01",
  "Payload": {
    "InitialNodeId": "N1",
    "SpeedMillimetersPerSecond": 1200,
    "BatteryPercent": 100,
    "IsOnline": true,
    "Capabilities": "Carry"
  }
}
```

车辆速度仍受区段速度上限约束。

## 6. 装载

```json
{
  "Id": "load-rgv-01",
  "AtMilliseconds": 0,
  "Order": 20,
  "Kind": "rgv.vehicle.load",
  "Target": "RGV-01",
  "Payload": {
    "LoadId": "PALLET-001"
  }
}
```

装载仅允许车辆在线、Idle 且位于节点时执行。

## 7. 分配显式路线

```json
{
  "Id": "route-rgv-01",
  "AtMilliseconds": 0,
  "Order": 30,
  "Kind": "rgv.route.assign",
  "Target": "RGV-01",
  "Payload": {
    "SegmentIds": ["S1", "S2"]
  }
}
```

要求：

- 第一段起点等于车辆当前节点；
- 每一段终点等于下一段起点；
- 所有区段已定义且启用；
- 路线段数不超过 `MaximumRouteSegments`。

## 8. 推进车辆

```json
{
  "Id": "advance-rgv-01",
  "AtMilliseconds": 1500,
  "Order": 0,
  "Kind": "rgv.vehicle.advance",
  "Target": "RGV-01",
  "Payload": {}
}
```

推进量由当前动作虚拟时间减去车辆最后更新时间决定。场景必须显式放置推进动作；仅让虚拟时钟经过不会自动修改车辆位置。

一次推进可以完成一个或多个区段，也可以停留在某个区段中间。

## 9. 在线状态

```json
{
  "Id": "offline-rgv-01",
  "AtMilliseconds": 2000,
  "Order": 0,
  "Kind": "rgv.vehicle.online.set",
  "Target": "RGV-01",
  "Payload": {
    "IsOnline": false
  }
}
```

离线不会删除路线和位置；恢复在线后若路线未完成，状态恢复为 Executing。S3 不执行生产任务恢复或路权释放。

## 10. 卸载

```json
{
  "Id": "unload-rgv-01",
  "AtMilliseconds": 3000,
  "Order": 0,
  "Kind": "rgv.vehicle.unload",
  "Target": "RGV-01",
  "Payload": {
    "ExpectedLoadId": "PALLET-001"
  }
}
```

卸载同样只允许在线、Idle 且位于节点时执行。

## 11. 常用断言

### 11.1 位于节点

```json
{
  "Id": "at-n3",
  "AtMilliseconds": 3000,
  "Order": 10,
  "Kind": "rgv.vehicle.at-node",
  "Target": "RGV-01",
  "Expected": "N3"
}
```

### 11.2 位于区段

```json
{
  "Id": "on-s2",
  "AtMilliseconds": 1500,
  "Order": 10,
  "Kind": "rgv.vehicle.on-segment",
  "Target": "RGV-01",
  "Expected": "S2"
}
```

### 11.3 状态

```json
{
  "Id": "state-idle",
  "AtMilliseconds": 3000,
  "Order": 20,
  "Kind": "rgv.vehicle.state",
  "Target": "RGV-01",
  "Expected": "Idle"
}
```

### 11.4 路线完成

```json
{
  "Id": "route-complete",
  "AtMilliseconds": 3000,
  "Order": 30,
  "Kind": "rgv.route.completed",
  "Target": "RGV-01",
  "Expected": true
}
```

### 11.5 区段占用

```json
{
  "Id": "s2-occupied",
  "AtMilliseconds": 1500,
  "Order": 20,
  "Kind": "rgv.segment.occupied-by",
  "Target": "S2",
  "Expected": "RGV-01"
}
```

### 11.6 载荷与电量

```json
{
  "Id": "load-check",
  "AtMilliseconds": 1500,
  "Order": 30,
  "Kind": "rgv.vehicle.load.equals",
  "Target": "RGV-01",
  "Expected": "PALLET-001"
}
```

```json
{
  "Id": "battery-check",
  "AtMilliseconds": 3000,
  "Order": 40,
  "Kind": "rgv.vehicle.battery.at-least",
  "Target": "RGV-01",
  "Expected": 95
}
```

## 12. 只读检查 API

```text
GET /api/simulation/virtual-rgv/runs/{runId}/status
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles/{vehicleId}
GET /api/simulation/virtual-rgv/runs/{runId}/vehicles/{vehicleId}/transport-snapshot
GET /api/simulation/virtual-rgv/runs/{runId}/segments
GET /api/simulation/virtual-rgv/runs/{runId}/segments/{segmentId}
GET /api/simulation/virtual-rgv/runs/{runId}/occupancy
GET /api/simulation/virtual-rgv/runs/{runId}/audit?take=100
```

这些接口只从运行 Checkpoint 中读取状态，不提供直接运动或路线修改入口。

## 13. Checkpoint 与 Replay

建议在以下节点创建 Checkpoint：

- 路线分配后、首次推进前；
- 车辆位于区段中间；
- 离线前后；
- 路线完成但卸载前。

恢复后继续执行相同虚拟时间动作，应得到相同位置、电量、审计顺序和 State Hash。

## 14. 常见错误

- `route does not start at the vehicle current node`：第一段起点不匹配；
- `route segments are not topologically continuous`：路线区段断开；
- `route can only be assigned to an online idle vehicle`：车辆离线、执行中或不在节点；
- `segment is disabled during movement`：路线执行期间区段不可用；
- `time cannot move backwards`：推进动作虚拟时间早于车辆最后更新时间；
- `vehicle already carries a load`：重复装载；
- `load identity does not match`：卸载期望载荷不一致。

## 15. 验收与安全

仓库验收必须完成两轮最新 exact Head 的 33/33。即使仓库测试全绿，也不代表真实 RGV 通讯、制动距离、机械互锁、电池模型、HIL 或现场安全验收完成。
