# EMS / RGV 统一调度第五阶段测试方案

## 1. 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportOptimizationTests.cs
```

### 1.1 最低电量保护

场景：

- EMS-LOW 电量 10%
- EMS-HIGH 电量 80%
- 请求最低电量 20%

预期：

- EMS-LOW 不进入候选集；
- 任务分配给 EMS-HIGH。

### 1.2 充电位容量

场景：

- 一个容量为 1 的充电站；
- 两辆低电量 EMS 同时请求充电。

预期：

- 第一辆状态为 Reserved；
- 第二辆状态为 WaitingForStation；
- 第一辆充电完成后第二辆自动升级为 Reserved；
- 第一辆恢复 Idle 并更新电量。

### 1.3 未取货任务换车

场景：

- EMS-01 正在前往取货点；
- EMS-01 故障；
- EMS-02 在线、空闲且能力匹配。

预期：

- 原车辆标记 Faulted；
- 原任务取消并释放预留；
- 生成新的接替请求号；
- 接替任务分配给 EMS-02；
- 原任务号与接替任务号可追溯。

### 1.4 已取货任务保护

场景：

- 车辆已完成装载；
- 进入 MovingToDestination；
- 发起故障任务换车。

预期：

- 返回 ManualRecoveryRequired；
- 原任务不被自动取消；
- 不创建接替任务。

### 1.5 效率指标

场景：

- 一辆低电量车辆进入 ChargingRequested；
- 一辆车辆处于 Executing。

预期：

- 在线车辆数为 2；
- 充电相关车辆数为 1；
- 低电量车辆数为 1；
- 利用率为 100%。

---

## 2. Host API 测试

### 2.1 注册充电站

```http
POST /api/transport/optimization/charging/stations
Content-Type: application/json

{
  "stationId": "CH-01",
  "nodeId": "CHARGE_NODE_01",
  "name": "一号充电位",
  "capacity": 1,
  "isOnline": true,
  "supportedVehicleKinds": [0, 1]
}
```

### 2.2 执行充电评估

```http
POST /api/transport/optimization/charging/evaluate
```

检查：

- 低电量空闲车辆生成计划；
- 执行中车辆只返回延迟或临界提示；
- 重复调用不会生成重复活动计划。

### 2.3 确认到达和完成

```http
POST /api/transport/optimization/charging/plans/{planId}/arrived
```

```http
POST /api/transport/optimization/charging/plans/{planId}/complete
Content-Type: application/json

{
  "batteryPercent": 90
}
```

### 2.4 故障任务转移

```http
POST /api/transport/optimization/executions/{requestId}/reassign
Content-Type: application/json

{
  "reason": "车辆驱动故障",
  "startImmediately": true
}
```

检查：

- 未取货任务返回 200；
- 已取货任务返回 409；
- 409 响应中 Decision 为 ManualRecoveryRequired。

---

## 3. Desktop 验收

检查菜单：

```text
充电与运行优化
```

检查页面：

- 充电站列表正常加载；
- 充电计划正常加载；
- 故障任务转移记录正常加载；
- 单车效率正常加载；
- “评估充电”不会重复创建计划；
- 页面刷新失败时显示明确错误信息。

---

## 4. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点关注：

- 新增记录类型 JSON 序列化；
- Avalonia XAML 编译；
- CommunityToolkit 命令生成；
- Host Controller 路由冲突；
- BackgroundService DI 构造函数解析。
