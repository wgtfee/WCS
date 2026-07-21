# EMS / RGV 统一调度第一阶段详细设计

## 1. 阶段目标

第一阶段建立可被 Application 层直接注入和调用的统一调度内核，不绑定具体 PLC、EMS 控制器或 RGV 厂商协议。

本阶段完成：

- EMS/RGV 统一车辆快照。
- 车辆状态注册与版本保护。
- 可用车辆过滤和候选排序。
- 空驶路径与载货路径规划。
- 路段原子预留及 TTL 清理。
- 车辆占用、任务完成和释放。
- `RequestId` 幂等派单。
- DI 注册、单元测试和测试设计文档。

本阶段不完成：

- PLC 命令下发。
- 控制器回执处理。
- 任务暂停、取消和人工接管。
- 闭塞区段方向锁。
- 路口冲突矩阵。
- 数据库持久化和重启恢复。
- 多实例调度。

## 2. 代码结构

```text
src/Wcs.Core/TransportScheduling/
├── TransportSchedulingModels.cs
├── TransportVehicleRegistry.cs
├── TransportVehicleSelector.cs
├── RouteReservationManager.cs
├── UnifiedTransportDispatchEngine.cs
└── TransportSchedulingRegistrationExtensions.cs

src/Wcs.Core.Tests/
└── UnifiedTransportDispatchEngineTests.cs
```

## 3. 模块接口

### 3.1 ITransportVehicleRegistry

```csharp
bool Upsert(TransportVehicleSnapshot snapshot);
bool TryGet(string vehicleId, out TransportVehicleSnapshot? snapshot);
IReadOnlyList<TransportVehicleSnapshot> GetAvailable(TransportDispatchRequest request);
bool TryMarkAssigned(string vehicleId);
bool TryMarkIdle(string vehicleId);
```

规则：

- 低版本快照不得覆盖高版本快照。
- 同版本时，较早的 `UpdatedAtUtc` 不得覆盖较新的状态。
- 派单使用 CAS 将 `Idle` 切换为 `Executing`。
- 完成时只允许将 `Executing` 切回 `Idle`。

### 3.2 ITransportVehicleSelector

输入：派单请求和已通过基本过滤的车辆列表。

输出：按评分升序排列的候选车辆及其空驶路径。

选择器不修改车辆状态，也不预留路段，因此可以独立替换为其他策略。

### 3.3 IRouteReservationManager

一次调用接收派单涉及的全部路段：

```csharp
TryReserve(ownerId, edgeIds, lease, out reservation)
```

在同一临界区内完成：

1. 清理过期预留。
2. 检查所有路段是否可用。
3. 创建预留记录。
4. 建立路段到预留的反向索引。
5. 通知 `TransportRouteCenter` 增加占用统计。

任一路段冲突时不写入任何预留记录。

### 3.4 IUnifiedTransportDispatchEngine

```csharp
Task<TransportDispatchResult> DispatchAsync(...);
bool TryGetAssignment(...);
bool Complete(string requestId);
```

第一阶段用 `SemaphoreSlim` 串行化派单关键区，优先保证确定性。后续可按线路或区域拆分锁粒度。

## 4. 派单算法

```text
Dispatch(request)
  ├─ Validate request
  ├─ Existing assignment? -> return existing
  ├─ Enter dispatch gate
  ├─ Existing assignment? -> return existing
  ├─ Registry.GetAvailable
  ├─ Selector.RankCandidates
  └─ foreach candidate
       ├─ Find loaded route
       ├─ Merge pickup + loaded edge ids
       ├─ TryReserve(all edges)
       ├─ TryMarkAssigned(vehicle)
       ├─ Save assignment
       └─ Return success
```

失败回滚：

- 预留成功但车辆占用失败：立即释放预留。
- 车辆占用成功但分配保存失败：车辆恢复空闲并释放预留。
- 所有候选均失败：返回失败，不保留部分状态。

## 5. DI 集成

`AddUnifiedTransportScheduling()` 注册：

- `TopologyGraph`
- `ITransportRouteCenter`
- `ITransportVehicleRegistry`
- `ITransportVehicleSelector`
- `IRouteReservationManager`
- `IUnifiedTransportDispatchEngine`

所有对象均为 Singleton，符合第一阶段单实例内存状态模型。

`ObjectTrackingCenter` 使用相同的 `TopologyGraph` 实例，使物料跟踪和运输调度共享统一节点与路径定义。

## 6. 典型调用

```csharp
var registry = serviceProvider.GetRequiredService<ITransportVehicleRegistry>();
var dispatch = serviceProvider.GetRequiredService<IUnifiedTransportDispatchEngine>();

registry.Upsert(new TransportVehicleSnapshot
{
    VehicleId = "EMS-01",
    Kind = TransportVehicleKind.Ems,
    State = TransportVehicleOperatingState.Idle,
    CurrentNodeId = "EMS-NODE-01",
    IsOnline = true,
    BatteryPercent = 85,
    Capabilities = TransportVehicleCapability.Carry,
    Version = 10
});

var result = await dispatch.DispatchAsync(new TransportDispatchRequest
{
    RequestId = "TASK-20260721-0001",
    SourceNodeId = "LOAD-01",
    DestinationNodeId = "UNLOAD-03",
    LoadId = "CARRIER-1001",
    RequiredCapability = TransportVehicleCapability.Carry,
    RequiredEdgeCapability = EdgeCapability.Transport,
    AllowedVehicleKinds = new HashSet<TransportVehicleKind>
    {
        TransportVehicleKind.Ems,
        TransportVehicleKind.Rgv
    }
});
```

成功结果只表示“调度分配与资源预留成功”，不表示 PLC 已接受或车辆已开始运行。执行层必须在第二阶段单独实现。

## 7. 第一阶段已知限制

1. 活动车辆、分配和预留均在内存中，进程重启后丢失。
2. 全局派单锁限制高并发，但可确保第一阶段行为清晰可验证。
3. 预留模型是路段互斥，不包含方向锁、追车安全距离和路口冲突。
4. 未根据 PLC 实际位置执行启动对账。
5. `Complete` 由上层明确调用，尚未由设备回执自动触发。
6. 未发布派单生命周期事件，待执行状态机落地时统一加入。

## 8. 第二阶段进入条件

- 第一阶段单元测试全部通过。
- 实际现场拓扑节点、边和闭塞区段编码确定。
- EMS/RGV 控制器能提供的状态、位置、任务号和回执字段确定。
- 明确 PLC 写入唯一通道。
- 确定异常时人工接管和恢复流程。
