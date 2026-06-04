# Step 9: WCS Runtime Engine V3 架构升级（工业级增强）

> 基于 V2（Step 8）架构审计中发现的 9 个关键风险点进行整改，从 Demo 可用升级到工业可部署。

---

## 升级总览

| # | 问题 | 严重度 | 方案 |
|---|------|--------|------|
| 1 | StateCenter 过于中心化 | 🔴 | 拆为 5 个独立 Manager |
| 2 | PLC Diff 粒度太粗 | 🔴 | 新增 SignalMapper 层 |
| 3 | DeviceManager 职责过重 | 🟡 | 拆为 4 个子组件 |
| 4 | WaitNode 事件丢失竞态 | 🔴 | State + Event 双保险 |
| 5 | EventReplay 重放实时数据 | 🟡 | 移除 PLC/设备实时事件 |
| 6 | ResourceLock 缺 Fence Token | 🔴 | 引入单调递增 FenceToken |
| 7 | AlarmCenter 缺设备屏蔽 | 🟡 | 新增 AlarmMaskManager |
| 8 | ObjectTracking 缺预占位 | 🔴 | 新增 ReservedNodeId |
| 9 | TaskScheduler 缺业务维度 | 🟡 | TaskPriority + TaskCategory |

---

## Phase 1 — SignalMapper 信号映射层

**问题：** PLC Diff 只给出字节偏移变化，业务模块需要自己解析 PLC 地址。

**解决：** 新增 `PlcSubsystem/SignalMapper/` 模块，将 `PlcBlockChangedEvent` 转换为业务信号事件。

### 新增文件

| 文件 | 说明 |
|------|------|
| `PlcSubsystem/SignalMapper/ISignalMapper.cs` | 信号映射器接口 |
| `PlcSubsystem/SignalMapper/SignalDefinition.cs` | 映射规则定义 |
| `PlcSubsystem/SignalMapper/SignalMapperEngine.cs` | 映射引擎实现 |
| `EventBus/Events/BusinessSignals.cs` | 业务信号事件类 |

### 映射示例

```
PLC 地址                    → 业务事件
DB1.DBX355.1 (Conveyor01Ready)  → ConveyorReadyChangedEvent { DeviceId="CV01", Ready=true }
DB1.DBW400   (Conveyor01Speed)  → ConveyorSpeedChangedEvent { DeviceId="CV01", Speed=1500 }
DB2.DBX10.0  (EStop)            → EmergencyStopEvent { DeviceId="ZONE_A" }
```

### SignalDefinition

```csharp
public class SignalDefinition
{
    string SignalId;            // 业务信号 ID
    string PlcName;             // 来源 PLC
    int BlockNumber;            // DB 块号
    int ByteOffset;             // 字节偏移
    int BitOffset = -1;         // 位偏移（-1=整个字节）
    string DataType;            // bool/byte/int/word/dword
    string TargetEventType;     // 目标事件 CLR 类型全名
    Dictionary<string, string> PropertyMappings; // 属性映射
    bool Enabled;               // 启用/禁用
}
```

---

## Phase 2 — StateCenter 解耦

**问题：** StateCenter 使用一个类管理 5 个 ConcurrentDictionary，事件通知、Diff 判断、订阅分发集中一处成为瓶颈。

**解决：** 拆分为 1 个门面 + 5 个独立 Manager。

### 新增文件

| 文件 | 说明 |
|------|------|
| `StateCenter/Implementation/DeviceStateManager.cs` | 设备状态管理器 |
| `StateCenter/Implementation/TaskStateManager.cs` | 任务运行时管理器 |
| `StateCenter/Implementation/AlarmStateManager.cs` | 报警状态管理器 |
| `StateCenter/Implementation/ObjectStateManager.cs` | 物体状态管理器 |
| `StateCenter/Implementation/PlcBlockStateManager.cs` | PLC 数据块管理器 |

### 架构变化

```
V2:                         V3:
StateCenter                 StateCenter (facade)
├── _deviceStates            ├── DeviceStateManager
├── _taskRuntimes            ├── TaskStateManager
├── _alarmStates     →       ├── AlarmStateManager
├── _objectStates            ├── ObjectStateManager
└── _plcBlockStates          └── PlcBlockStateManager
                                 各 Manager 独立管理:
                                 - ConcurrentDictionary
                                 - KeyedEventChannel
                                 - Diff 判断
                                 - 批量更新
```

**兼容性：** `IStateCenter` 接口完全不变，外部代码不受影响。

---

## Phase 3 — WaitNode State+Event 双保险

**问题：** WaitNode 仅通过 EventBus 订阅设备状态变化，若状态在订阅前已到达，将永远等不到。

**解决：** 执行流程改为先查 StateCenter → 满足则立即返回 → 不满足才订阅 EventBus。

```
ExecuteWaitSignalAsync(node)
  │
  ├── 解析 targetDevice, targetStatus
  │
  ├── 1. State Check:
  │     _stateCenter.GetDeviceState(targetDevice)
  │       → 状态已满足？→ 直接 return true ✅
  │
  └── 2. Event Subscription:
        EventBus.Subscribe(DeviceStateChangedEvent)
          → 等待超时或状态匹配
```

**修改文件：** `ChainExecutionEngine.cs`（构造函数加可选 IStateCenter 参数）

---

## Phase 4 — ResourceLock FenceToken

**问题：** 锁 TTL 过期后旧持有者仍可能继续执行，导致两个任务同时控制同一设备。

**解决：** 引入单调递增的 Fence Token。

```csharp
// 每次获取锁分配唯一递增 token
var fenceToken = Interlocked.Increment(ref _fenceCounter);

// 校验方法 — 设备操作前调用
bool ValidateFenceToken(string resourceId, long fenceToken)
{
    // 检查 token 是否匹配当前锁持有者
    return entry.FenceToken == fenceToken && !expired;
}
```

**典型流程：**
```
1. ThreadA: TryAcquireAsync("CV01") → FenceToken=100
2. ThreadA GC 停顿 10s → TTL 过期 → 锁释放
3. ThreadB: TryAcquireAsync("CV01") → FenceToken=101 ✅
4. ThreadA 恢复 → ValidateFenceToken("CV01", 100) → ❌ 拒绝
5. ThreadA 不能控制 CV01 — 工业事故避免
```

**修改文件：** `ResourceLockManager.cs`

---

## Phase 5 — TaskScheduler 多维度优先级

**问题：** 仅一个 int Priority 维度，紧急订单、恢复任务、人工任务无法区分。

**解决：** 新增 TaskPriority 枚举和 TaskCategory 枚举，调度器做双维度排序。

```csharp
public enum TaskPriority { Low=1, Normal=2, High=3, Emergency=4 }
public enum TaskCategory { Production=0, Recovery=1, Manual=2 }

// 排序算法
Weight = CategoryWeight(Recovery=10000) + PriorityWeight(Emergency=4) + LegacyPriority
```

**修改文件：**
- `TaskEngine/Context/TaskContext.cs` — 新增 PriorityLevel, Category 属性
- `TaskEngine/Scheduler/TaskScheduler.cs` — 双维排序算法

---

## Phase 6 — AlarmCenter AlarmMaskManager

**问题：** 设备维修时故障报警持续触发，无法临时关闭。

**解决：** 新增报警屏蔽管理器，支持设备级别和报警码级别的屏蔽规则。

### 新增文件

| 文件 | 说明 |
|------|------|
| `AlarmCenter/Masking/AlarmMaskRule.cs` | 屏蔽规则（DeviceId/AlarmCode/时间段） |
| `AlarmCenter/Masking/AlarmMaskManager.cs` | 屏蔽管理器 |

### 屏蔽规则优先级

```
1. DeviceId + AlarmCode 精确匹配
2. DeviceId 匹配（屏蔽该设备所有报警）
3. AlarmCode 匹配（全局屏蔽某报警码）
4. 全局规则（屏蔽所有）
```

**修改文件：** `AlarmCenter.cs` — 在 `OnDebounceConfirmedRaise` 中插入屏蔽检查

---

## Phase 7 — ObjectTracking 预占位

**问题：** 无预约机制，托盘 A 还在 CV02 时 CV03 可能已经分配给托盘 B，导致双托盘冲突。

**解决：** 新增节点预占位机制。

### ObjectState 新增属性

```csharp
public string? ReservedNodeId { get; set; }  // 预约的节点
public List<string>? Route { get; set; }     // 完整路径
```

### ObjectTrackingCenter 新增方法

```csharp
bool ReservePosition(objectId, nodeId, route?)  // 预约 → 检查冲突
bool ConfirmPosition(objectId, currentNodeId)   // 到达确认 → 释放预占
bool CancelReservation(objectId)                // 取消预约
```

### TopologyGraph 新增方法

```csharp
bool IsNodeReserved(nodeId)                      // 节点是否被预约
void SetNodeOccupied(nodeId, occupied)           // 标记节点占用
void SetNodeOccupiedBy(nodeId, objectId)         // 标记占用者
```

**修改文件：** `StateModels.cs`, `ObjectTrackingCenter.cs`, `TopologyGraph.cs`

---

## Phase 8 — EventReplay 白名单精炼

**问题：** 系统恢复时重放 PLC 状态事件，但 PLC 是实时状态，重放过期数据会导致脏恢复。

**解决：** 白名单中移除实时状态事件。

```csharp
// V2（重放一切）
ReplayableEventTypes = {
    DeviceStateChangedEvent,   ← 实时，不应重放
    TaskStateChangedEvent,     ✓ 保留
    ObjectLocationChangedEvent,✓ 保留
    PlcBlockChangedEvent       ← 实时，不应重放
}

// V3（只重放有状态且需要恢复的）
ReplayableEventTypes = {
    TaskStateChangedEvent,     // 任务状态需要恢复
    ObjectLocationChangedEvent,// 物体位置需要恢复
    AlarmRaisedEvent,          // 报警需要恢复
    AlarmRecoveredEvent,       // 报警恢复需要恢复
}
```

**修改文件：** `EventBus/Persistence/EventReplayService.cs`

---

## Phase 9 — DeviceManager 拆分

**问题：** DeviceManager 同时负责注册、命令、状态同步、事件发布、健康检查，职责过重。

**解决：** 拆分为 4 个子组件，DeviceManager 作为门面委托。

### 新增文件

| 文件 | 职责 |
|------|------|
| `DeviceCenter/DeviceRegistry.cs` | 设备注册/注销/查询 |
| `DeviceCenter/DeviceCommandDispatcher.cs` | 启动/停止/复位/暂停/恢复 |
| `DeviceCenter/DeviceStateSynchronizer.cs` | 状态同步 |
| `DeviceCenter/DeviceHealthMonitor.cs` | 健康检查/心跳 |

### 架构变化

```
V2:                         V3:
DeviceManager               DeviceManager (facade)
├── _devices                 ├── DeviceRegistry
├── _eventHandlers    →      ├── DeviceCommandDispatcher
├── 所有方法                  ├── DeviceStateSynchronizer
                              └── DeviceHealthMonitor
```

**兼容性：** `IDeviceManager` 接口完全不变。

---

## 修改文件总览

| 文件 | 操作 |
|------|------|
| **新建 19 个文件** | |
| `PlcSubsystem/SignalMapper/ISignalMapper.cs` | 新建 |
| `PlcSubsystem/SignalMapper/SignalDefinition.cs` | 新建 |
| `PlcSubsystem/SignalMapper/SignalMapperEngine.cs` | 新建 |
| `EventBus/Events/BusinessSignals.cs` | 新建 |
| `StateCenter/Implementation/DeviceStateManager.cs` | 新建 |
| `StateCenter/Implementation/TaskStateManager.cs` | 新建 |
| `StateCenter/Implementation/AlarmStateManager.cs` | 新建 |
| `StateCenter/Implementation/ObjectStateManager.cs` | 新建 |
| `StateCenter/Implementation/PlcBlockStateManager.cs` | 新建 |
| `AlarmCenter/Masking/AlarmMaskRule.cs` | 新建 |
| `AlarmCenter/Masking/AlarmMaskManager.cs` | 新建 |
| `DeviceCenter/DeviceRegistry.cs` | 新建 |
| `DeviceCenter/DeviceCommandDispatcher.cs` | 新建 |
| `DeviceCenter/DeviceStateSynchronizer.cs` | 新建 |
| `DeviceCenter/DeviceHealthMonitor.cs` | 新建 |
| **修改 10 个文件** | |
| `StateCenter/Implementation/StateCenter.cs` | 重构为门面委托模式 |
| `TaskEngine/Chain/ChainExecutionEngine.cs` | State+Event 双保险 |
| `ResourceLock/ResourceLockManager.cs` | 加 FenceToken |
| `TaskEngine/Context/TaskContext.cs` | 加 PriorityLevel/Category |
| `TaskEngine/Scheduler/TaskScheduler.cs` | 双维排序 |
| `AlarmCenter/AlarmCenter.cs` | 集成屏蔽检查 |
| `StateCenter/Models/StateModels.cs` | ObjectState 加 ReservedNodeId/Route |
| `ObjectTracking/ObjectTrackingCenter.cs` | 加预占位方法 |
| `ObjectTracking/Topology/TopologyGraph.cs` | 加节点预留管理 |
| `EventBus/Persistence/EventReplayService.cs` | 精炼白名单 |

---

## 验证结果

- **`dotnet build`** — 0 errors
- **`dotnet test`** — 108/108 全部通过
- 所有 9 个 Phase 修改向后兼容（接口未变）
