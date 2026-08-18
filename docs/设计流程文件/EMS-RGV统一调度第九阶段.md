# EMS / RGV 统一调度第九阶段设计

## 1. 阶段目标

第九阶段将前八阶段已经具备的统一派单、滚动路权、死锁检测、充电、故障转移、PLC 驱动和现场联调能力提升为可长期运行的生产级调度闭环。

本阶段重点解决：

- 多任务同时到达时的竞争顺序；
- 长时间等待任务不被永久饿死；
- 交期、生产订单和恢复任务的动态优先级；
- 目标站点满载和排队拥堵；
- 单轨 EMS/RGV 相向会车和同向编队；
- 故障车辆的自动接管与物理占用保护；
- 无副作用调度试算和决策回放；
- 运行趋势、瓶颈识别和现场参数整定。

## 2. 总体结构

```text
MES / WMS / 人工任务
        ↓
TransportProductionDispatchService
        ├── DynamicPriorityService
        ├── StationCongestionService
        ├── DispatchDecisionStore
        └── UnifiedTransportDispatchEngine
                    ↓
          DispatchAdmissionPolicy
                    ↓
     SingleTrackDispatchAdmissionPolicy
                    ↓
       TransportSingleTrackCoordinator
                    ↓
       TransportTrafficCoordinator
                    ↓
        RouteReservationManager
                    ↓
        Execution / PLC Driver
```

生产调度层不取代原调度引擎。它只负责竞争队列、优先级、拥堵和准入，最终路径规划、车辆占用和路权预留仍由前几阶段的正式引擎完成。

## 3. 动态优先级

有效优先级计算：

```text
基础任务优先级
+ 生产订单优先级
+ 等待老化加分
+ 交期紧迫加分
+ 故障恢复任务加分
- 目标站点拥堵惩罚
```

等待老化设置最大值，防止数值无限增长；即使低优先级任务持续等待，也能够逐渐进入队首，避免任务饿死。

可整定参数包括：

- 每分钟老化加分；
- 最大老化加分；
- 交期紧迫窗口及加分；
- 恢复任务加分；
- 站点排队惩罚；
- 满载站点惩罚；
- 单周期最大派单数；
- 单轨反向等待老化时间；
- 趋势采集间隔和保留点数；
- 故障接管冷却时间。

参数使用版本号进行乐观并发控制，并写入 TransportJournal SQL 存储。

## 4. 多任务竞争队列

队列状态：

```text
Queued
Dispatching
Assigned
WaitingForStation
WaitingForTraffic
WaitingForVehicle
Failed
Cancelled
```

每个派单周期：

1. 重新计算全部可竞争任务的有效优先级；
2. 按优先级、入队时间、RequestId 稳定排序；
3. 取本周期允许的最大任务数；
4. 检查目标站点准入；
5. 调用统一派单引擎；
6. 根据失败原因进入站点、交通或车辆等待状态；
7. 写入决策记录。

生产队列使用 RequestId 幂等，重复提交不会生成第二个任务。

## 5. 站点拥堵

站点定义：

```text
StationId
Name
Capacity
MaximumQueuedTasks
Enabled
```

运行状态：

```text
OccupiedCount
QueuedTaskCount
UtilizationPercent
```

准入规则：

- 站点停用：拒绝；
- 占用达到容量：拒绝；
- 排队达到上限：拒绝；
- 未满载：允许，但按占用率和排队数扣减任务优先级。

站点实时占用由 MES、WCS 业务状态或 PLC 映射更新，配置定义写入数据库。

## 6. 单轨会车

单轨定义：

```text
SectionId
OrderedNodeIds
TrafficResourceId
Capacity
MaximumSameDirectionConvoy
Enabled
```

有序节点用于判断方向：

```text
N1 → N2 → N3 = Forward
N3 → N2 → N1 = Reverse
```

门禁规则：

- 区段为空：按动态优先级和等待时间选择队首方向；
- 区段已有车辆：只允许相同方向、容量未满的车辆加入；
- 反向车辆等待超过整定时间：停止继续放入新同向车辆，等待区段清空后切换方向；
- 不直接释放 TrafficCoordinator 的物理路权；
- 如果 TrafficResourceId 存在已确认物理占用，故障接管也不能释放单轨许可。

因此逻辑会车不会破坏第四阶段的物理占用保护。

## 7. 派单门禁扩展点

新增：

```text
ITransportDispatchAdmissionPolicy
```

统一派单引擎在路径计算完成、路权预留之前调用所有门禁策略。

策略生命周期：

```text
Evaluate
OnAssigned
OnCompleted
CancelRequest
```

第九阶段实现单轨门禁。后续可继续增加：

- 洁净区门禁；
- 防火门联锁；
- 电梯容量；
- 工艺禁行时段；
- 人车混行区域限速准入。

## 8. 故障车辆接管

后台每 5 秒检查非终态任务：

- 车辆在线且无故障：跳过；
- 车辆离线或 Faulted：进入接管评估；
- 尚未取货：调用第五阶段 ReassignmentService 自动换车；
- 已装载、装载中或卸载中：保持人工恢复；
- 原车辆存在已确认物理占用：保留单轨许可，不自动清场；
- 使用冷却时间避免同一任务反复接管。

故障接管不会绕过既有的“装载后禁止自动转移”规则。

## 9. 无副作用试算和决策回放

DryRun：

- 读取当前队列、站点和整定参数；
- 计算任务排名和站点准入；
- 不标记车辆；
- 不预留路权；
- 不写 PLC；
- 不修改队列状态。

每次真实派单记录：

```text
RequestId
EffectivePriority
ResultState
VehicleId
Reason
CompetingRequestIds
OccurredAtUtc
```

Desktop 可查看最近决策，解释某个任务为什么先派、等待或失败。

## 10. 运行趋势

趋势点包含：

- 队列长度；
- 等待站点任务数；
- 等待交通任务数；
- 故障车辆数；
- 单轨等待车辆数；
- 站点最高利用率；
- 车队利用率；
- 任务完成率。

后台按整定间隔采集，写入 TransportJournal。API 支持任意时间范围汇总，Desktop 默认展示最近 24 小时。

## 11. Host API

基础路径：

```text
/api/transport/production
```

接口：

```text
GET  /tuning
PUT  /tuning
GET  /stations
PUT  /stations/{stationId}
POST /stations/{stationId}/runtime
GET  /single-track
PUT  /single-track/{sectionId}
GET  /queue
POST /queue
POST /queue/{requestId}/cancel
POST /queue/{requestId}/complete
POST /dispatch-cycle
GET  /dry-run
GET  /decisions
GET  /trends
POST /trends/capture
GET  /fault-takeover
POST /fault-takeover/evaluate
```

整定参数、站点定义和单轨定义使用 ChangeConfiguration 双人审批。运行态队列、试算、趋势和安全接管不要求额外审批。

## 12. Desktop

新增菜单：

```text
生产级调度
```

页面展示：

- 生产竞争队列；
- 动态优先级；
- 站点拥堵；
- 单轨方向、活动许可和等待车辆；
- 无副作用调度试算；
- 决策回放；
- 故障接管；
- 运行趋势。

Desktop 不提供参数、站点和单轨定义修改入口，避免绕过审批。
