# EMS / RGV 统一调度第五阶段

## 1. 阶段目标

第五阶段在第四阶段多车交通控制基础上增加：

1. 车辆最低电量保护；
2. 充电站容量和等待队列；
3. 空闲低电量车辆自动生成充电计划；
4. 故障车辆未取货任务自动换车；
5. 已取货任务现场恢复保护；
6. 车队利用率、完成率、等待和故障指标；
7. Host API 与 Desktop 监控页面。

本阶段仍然保持调度核心不直接依赖具体 EMS、RGV 或 PLC 协议。

---

## 2. 最低电量保护

`TransportDispatchRequest` 新增：

- `MinimumBatteryPercent`
- `AllowLowBatteryOverride`
- `RequiredVehicleId`

默认最低派单电量为 20%。

普通生产任务只选择：

- 在线；
- 空闲；
- 能力匹配；
- 车辆类型匹配；
- 电量达到最低值；
- 当前位置有效的车辆。

紧急任务只有显式设置 `AllowLowBatteryOverride=true` 才能绕过电量保护。

---

## 3. 充电调度

### 3.1 默认策略

| 参数 | 默认值 |
|---|---:|
| 充电触发阈值 | 30% |
| 临界电量 | 15% |
| 最低派单电量 | 20% |
| 推荐恢复电量 | 80% |

### 3.2 安全边界

自动充电只处理空闲车辆。

执行中车辆达到低电量时：

- 不取消当前运输任务；
- 不自动转向充电站；
- 生成延迟充电或临界电量结果；
- 当前任务结束后再进入充电调度。

### 3.3 充电计划状态

```text
WaitingForStation
Reserved
Charging
Completed
Cancelled
Faulted
```

车辆进入活动充电计划后，状态变为：

```text
ChargingRequested
WaitingForCharge
Charging
```

这些状态全部退出普通生产派单候选集。

### 3.4 充电位容量

每个充电站配置：

- StationId
- NodeId
- Capacity
- 支持的车辆类型
- 在线状态

容量已满时，车辆进入等待队列。队列升级顺序：

1. 临界电量优先；
2. 剩余电量更低优先；
3. 请求时间更早优先。

---

## 4. 故障车辆任务转移

### 4.1 可自动换车

以下状态允许自动换车：

- Assigned
- MovingToPickup
- WaitingForRoute、Paused、Faulted 且尚未到达取货点

处理流程：

```text
标记原车辆 Faulted
        ↓
取消原执行任务并释放未来路权
        ↓
根据原派单结果重建新请求
        ↓
选择同类型健康车辆
        ↓
创建并启动接替任务
        ↓
记录原任务号和接替任务号
```

### 4.2 禁止自动换车

以下状态禁止自动换车：

- Loading
- MovingToDestination
- Unloading
- 已经过取货节点的暂停或故障任务

原因是载荷已经与原车辆形成物理绑定。系统只记录：

```text
ManualRecoveryRequired
```

现场必须确认：

- 载荷实际位置；
- 原车辆是否仍夹持或承载载荷；
- 是否需要人工卸载；
- 新任务从哪个节点重新开始。

---

## 5. 运行效率指标

实时指标包括：

- 在线车辆数；
- 空闲车辆数；
- 执行中车辆数；
- 充电相关车辆数；
- 低电量车辆数；
- 总执行任务数；
- 完成、故障、等待任务数；
- 自动换车次数；
- 人工恢复次数；
- 车队利用率；
- 任务完成率；
- 平均完成耗时；
- 单车完成、故障和等待统计。

车队利用率当前定义：

```text
(执行中车辆 + 充电相关车辆) / 在线车辆
```

后续历史报表可直接沿用 `TransportPerformanceSnapshot` 模型写入 SQL 或时序数据库。

---

## 6. Host API

基础路径：

```text
/api/transport/optimization
```

主要接口：

```text
GET    /charging/policy
GET    /charging/stations
POST   /charging/stations
DELETE /charging/stations/{stationId}
GET    /charging/plans
POST   /charging/evaluate
POST   /charging/vehicles/{vehicleId}/evaluate
POST   /charging/plans/{planId}/arrived
POST   /charging/plans/{planId}/complete
POST   /charging/plans/{planId}/cancel

GET    /reassignments
POST   /executions/{requestId}/reassign

GET    /metrics
```

故障换车接口应由具备调度操作权限的账号调用，并记录操作人和原因。当前 Core 模型已保留原因字段，权限体系由 Host 统一接入。

---

## 7. Desktop 页面

新增菜单：

```text
充电与运行优化
```

页面包含：

- 充电站容量；
- 当前预留、充电和排队数量；
- 充电计划状态；
- 故障任务转移链路；
- 单车效率；
- 车队利用率和完成率。

Desktop 默认不开放故障换车按钮，避免监控账号误操作现场任务。

---

## 8. 后台巡检

`TransportOptimizationHostedService` 每 10 秒评估一次低电量车辆。

巡检只会：

- 为符合条件的空闲车辆创建充电计划；
- 更新充电请求状态。

不会：

- 自动取消执行中任务；
- 自动对已装载任务换车；
- 自动强制释放第四阶段确认过的物理占用资源。

---

## 9. 第六阶段建议

第五阶段完成后，建议第六阶段处理：

- 调度配置持久化；
- 充电计划和换车记录 SQL 落库；
- 权限、审计和双人确认；
- 运行日报、班次报表和瓶颈分析；
- 仿真压测与现场参数标定；
- EMS/RGV 实际 PLC 驱动实现。
