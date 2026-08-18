# EMS / RGV 统一调度第四阶段

## 1. 阶段目标

第四阶段解决多车运行时的交通互斥和循环等待问题，覆盖：

- 交叉口冲突；
- 单轨区会车冲突；
- 合流点排队；
- 闭塞区优先级和先到先服务；
- 真实 Wait-For Graph；
- 死锁检测；
- 安全受害者选择和死锁环打断；
- Desktop 交通控制监控。

本阶段不允许通过强制释放车辆正在物理占用的资源来解除死锁。

## 2. 交通资源模型

多个存在冲突关系的 Edge 映射为同一个交通资源：

```text
交叉口 X-01
  ├─ E-NORTH
  ├─ E-SOUTH
  ├─ E-EAST
  └─ E-WEST
```

只要 X-01 的容量为 1，上述任意一个方向进入后，其他冲突方向必须等待。

单轨区采用同样模型：整段单轨走廊可配置为一个 `SingleTrack` 资源，从而禁止相向车辆同时进入。

## 3. 获取顺序

```text
完整路径规划
    ↓
计算滚动窗口 Edge
    ↓
映射 Traffic Resource
    ↓
原子获取交通资源
    ↓
原子预留闭塞 Edge
    ↓
生成车辆执行命令
```

交通资源获取失败时，不会继续进行闭塞 Edge 预留，任务保持等待。

## 4. 排队策略

资源释放后的竞争顺序：

1. 业务优先级高者优先；
2. 相同优先级时，等待时间长者优先；
3. 等待时间按照 AgingInterval 增加有效优先级，避免长期饥饿；
4. 最后按照 OwnerId 稳定排序，保证结果可重复。

## 5. Wait-For Graph

等待图只记录真实关系：

```text
REQ-A 等待 R2，R2 由 REQ-B 持有
REQ-B 等待 R1，R1 由 REQ-A 持有

REQ-A → REQ-B → REQ-A
```

禁止根据“系统中存在多个锁持有者”推断等待关系，否则会产生假死锁。

## 6. 死锁检测

检测器对等待图执行深度优先搜索，遇到当前递归栈中的节点时生成死锁环。

死锁环 ID 由规范化 Owner 顺序计算，避免同一个环因起点不同被重复上报。

## 7. 安全解锁

受害者选择规则：

1. 优先级最低；
2. 优先级相同时，创建时间较晚；
3. 最后按照 OwnerId 稳定选择。

处置动作：

1. 暂停受害任务并产生 Stop 逻辑命令；
2. 撤销受害任务的等待请求；
3. 释放未确认物理占用的未来交通资源；
4. 运动状态保留靠近车辆的第一条 Edge 作为安全缓冲；
5. 已确认物理占用的资源继续保留；
6. 再次检测等待图；
7. 环仍存在时进入人工处理。

## 8. 物理占用确认

调度预留不等于物理占用。EMS 控制器、RGV PLC 或位置推断层应调用：

```http
POST /api/transport/traffic/occupancy
```

进入资源时设置 `Occupied=true`，安全退出后设置 `Occupied=false`。

租约过期不会自动释放已确认物理占用的资源。

## 9. API

```text
GET    /api/transport/traffic
GET    /api/transport/traffic/resources
GET    /api/transport/traffic/holds
GET    /api/transport/traffic/waits
GET    /api/transport/traffic/deadlocks
GET    /api/transport/traffic/incidents
POST   /api/transport/traffic/resources
DELETE /api/transport/traffic/resources/{resourceId}
POST   /api/transport/traffic/occupancy
POST   /api/transport/traffic/deadlocks/{cycleId}/resolve
```

## 10. Desktop 页面

新增“交通控制与死锁”页面，包括：

- 交通资源定义；
- 当前资源占用；
- 物理占用确认；
- 等待任务及阻塞数量；
- 死锁环；
- 自动处置记录。

页面暂不开放强制释放按钮，避免现场误操作。

## 11. 后续阶段

第五阶段建议进入：

- 充电任务调度；
- 低电量任务迁移；
- 车辆故障后的任务重分配；
- 交通吞吐量和等待时长统计；
- 调度策略参数化与仿真评估。
