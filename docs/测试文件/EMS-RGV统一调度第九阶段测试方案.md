# EMS / RGV 统一调度第九阶段测试方案

## 1. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

新增测试文件：

```text
src/Wcs.Core.Tests/TransportProductionSchedulingTests.cs
```

## 2. Core 自动化测试

### 2.1 参数版本控制

验证：

- Version=0 首次保存后变为 Version=1；
- 使用旧 ExpectedVersion 保存返回 VersionConflict；
- 新服务实例能够从 TransportJournal 恢复参数。

### 2.2 动态优先级

验证公式：

```text
基础优先级
+ 生产订单优先级
+ 老化加分
+ 交期加分
+ 恢复任务加分
- 站点拥堵惩罚
```

检查最大老化限制、过期交期和满载惩罚。

### 2.3 站点拥堵

验证：

- OccupiedCount 达到 Capacity 时拒绝；
- QueuedTaskCount 达到 MaximumQueuedTasks 时拒绝；
- 未满载时返回可用及拥堵惩罚；
- 停用站点拒绝；
- 站点定义可恢复。

### 2.4 单轨会车

验证：

- Forward 首车获得许可；
- Forward 活动时 Reverse 被拒绝；
- 同向车辆在容量范围内可编队；
- 反向车辆等待超过整定时间后停止继续放入新同向车辆；
- 区段清空后方向可切换；
- 有 OccupancyConfirmed 的 TrafficResource 不允许释放许可。

### 2.5 派单门禁

验证：

- 门禁在路权预留前执行；
- 单轨反向任务返回包含单轨原因的失败；
- 成功派单后提交许可；
- Complete 后释放逻辑许可；
- 原有不使用门禁的测试保持兼容。

### 2.6 多任务竞争

验证：

- 同一 RequestId 重复入队保持幂等；
- 有效优先级最高任务先派；
- 单周期不超过 MaximumDispatchPerCycle；
- 满站任务进入 WaitingForStation；
- 单轨或路权失败进入 WaitingForTraffic；
- 无车辆进入 WaitingForVehicle；
- 成功任务记录 AssignedVehicleId。

### 2.7 无副作用试算

验证 DryRun：

- 返回稳定排序；
- 返回站点准入说明；
- 不调用统一派单引擎；
- 不修改队列状态；
- 不预留路权、不标记车辆、不写 PLC。

### 2.8 趋势

验证：

- 队列、站点、单轨、故障车辆和性能指标正确采集；
- 时间范围汇总正确；
- 保留点数超过上限时淘汰最旧数据；
- 趋势点写入 ProductionTrend Journal。

### 2.9 故障接管

验证：

- 在线健康车辆跳过；
- 离线或 Faulted 车辆触发 ReassignmentService；
- 取货前任务成功接替；
- 已装载任务返回 ManualRecoveryRequired；
- 无替代车辆返回 NoAlternativeVehicle；
- 物理占用未清除时返回 WaitingForPhysicalClearance；
- 冷却窗口内不重复接管。

## 3. Host API 测试

基础路径：

```text
/api/transport/production
```

### 3.1 配置审批

对以下目标创建 ChangeConfiguration 审批：

```text
production:tuning
production-station:{StationId}
single-track:{SectionId}
```

验证：

- 未认证或未审批返回 409；
- 申请人与审批人不能相同；
- 审批目标不一致不能执行；
- 审批号只能执行一次；
- 执行成功或失败均写审计结果。

### 3.2 队列与派单

验证：

```http
POST /queue
GET  /queue
POST /dispatch-cycle
POST /queue/{requestId}/cancel
POST /queue/{requestId}/complete
```

重复提交同一 RequestId 不生成重复队列项。

### 3.3 试算与回放

验证：

```http
GET /dry-run
GET /decisions
```

调用前后车辆、路权和队列状态保持不变；真实派单后 Decisions 包含竞争任务和失败原因。

### 3.4 趋势与接管

验证：

```http
GET  /trends
POST /trends/capture
GET  /fault-takeover
POST /fault-takeover/evaluate
```

趋势支持 ISO 8601 时间范围；故障接管不能绕过装载后人工恢复规则。

## 4. Desktop 验收

菜单：

```text
生产级调度
```

检查：

- 队列、动态优先级和等待原因；
- 站点容量、占用、排队和利用率；
- 单轨方向、许可和等待数；
- 无副作用试算；
- 决策回放；
- 故障接管结果；
- 24 小时趋势。

页面不得包含参数、站点定义或单轨定义直接修改入口。

## 5. 现场异常场景

### 5.1 站点满载

目标站点容量达到上限，任务保持 WaitingForStation；释放一个位置后下一周期应自动重新竞争。

### 5.2 单轨相向车辆

Forward 已进入区段，Reverse 必须等待；已确认物理占用不得通过故障接管或 Complete 误释放。

### 5.3 低优先级长期等待

持续注入高优先级任务，低优先级任务应通过老化逐渐提升，最终进入队首。

### 5.4 故障发生在取货前

原车 Faulted，存在备用车，应创建接替任务并保留完整重分配记录。

### 5.5 故障发生在装载后

不得自动换车，结果必须为 ManualRecoveryRequired。

### 5.6 Host 重启

整定参数、站点、单轨定义和趋势可从 SQL Journal 恢复；不自动恢复车辆运动。
