# Step 7: Core Architecture Redesign — StateCenter, AlarmCenter, TaskChainEngine, ObjectTrackingCenter

## 背景

用户在完成 Avalonia 桌面客户端后，要求对四个核心模块进行架构升级。用户强调这些模块是"WCS 的灵魂"，其设计质量直接决定项目长期维护成本。

**优先级**：四个模块同为第一梯队。依赖关系：StateCenter（数据基座）→ AlarmCenter（依赖设备状态）→ TaskChainEngine（依赖 StateCenter 和 ResourceLock）→ ObjectTrackingCenter（依赖 TaskChainEngine 的任务状态事件）。

## 设计原则

1. **StateCenter 是 in-memory system truth，不是 cache** — 所有模块通过它读写状态
2. **AlarmCenter 的防抖和风暴抑制是硬要求** — 避免"一分钟几万条报警，操作员不再相信报警"
3. **TaskChain 是 DAG，不是线性队列** — 支持 Action/Wait/Parallel/Delay/Decision 五种节点
4. **ObjectTracking 是数字孪生核心** — 追踪每个物体的完整位置历史

---

## 1. StateCenter 重构

### 改造点
- **diff 通知抑制**：UpdateDeviceState 等检查状态是否实质变化，未变则不通知
- **ImmutableDictionary 快照**：`GetSnapshot<T>()` 返回点时间一致的只读视图
- **BatchScope**：`AsyncLocal<Stack<BatchScope>>` 实现，批量更新合并为一次通知
- **KeyedEventChannel**：per-key `IObservable` 风格订阅，替代全局 IStateChangeListener
- **ISnapshotProvider**：StateCenter 实现此接口以支持 RecoveryManager 多模块编排

### 新增文件
| 文件 | 说明 |
|------|------|
| `Common/Interfaces/ISnapshotProvider.cs` | 模块快照接口（ModuleName, CaptureSnapshotAsync, RestoreSnapshotAsync） |
| `StateCenter/Features/BatchScope.cs` | AsyncLocal 批量作用域，StateChangeRecord 泛型记录 |
| `StateCenter/Features/KeyedEventChannel.cs` | per-key 事件通道，支持 Subscribe/Publish |

### 修改文件
| 文件 | 改动 |
|------|------|
| `StateCenter/Interfaces/IStateCenter.cs` | 新增 GetSnapshot\<T\>(), BeginBatch(), WatchDevice/WatchTask/WatchAlarm/WatchObject |
| `StateCenter/Implementation/StateCenter.cs` | diff 检测、BatchScope 集成、EventBus 可选发布、ISnapshotProvider 实现 |

---

## 2. AlarmCenter 5 层架构

### 5 层处理管线
```
Raw Signal → AlarmDebounceEngine → AlarmStormGuard → AlarmStateMachine
  → AlarmAggregationEngine → EventBus
```

### 5 状态报警状态机
```
Normal ──raise──→ PendingRaise ──confirmed──→ Active ──ack──→ Acknowledged
  ↑                    │                          │                  │
  └──canceled──────────┘    ←──rebounce─────────┘                  │
                                                                    │
Recovered ←──confirmed── PendingRecover ←──recover───┘
  ↑                        │
  └───────rebounce─────────┘
```

7 条合法转换，非法转换为 InvalidOperationException。

### 新增文件
| 文件 | 说明 |
|------|------|
| `AlarmCenter/Models/AlarmStateMachine.cs` | 5 状态枚举 + 合法转换验证 + 状态查询 |
| `AlarmCenter/Engine/AlarmDebounceEngine.cs` | DelayRaise/DelayRecover 计时器，抖动重置 |
| `AlarmCenter/Engine/AlarmStormGuard.cs` | 滑动窗口速率限制（per-code + global），风暴模式自动抑制 |
| `AlarmCenter/Engine/AlarmAggregationEngine.cs` | Device+AlarmGroup 根因归并，子报警抑制，根因恢复释放 |

### 修改文件
| 文件 | 改动 |
|------|------|
| `AlarmCenter/AlarmCenter.cs` | 5 层管线集成，防抖回调 → 风暴检测 → 状态转换 → 聚合 |
| `StateCenter/Models/StateModels.cs` | AlarmStatusEnum 扩展为 6 值（Normal/PendingRaise/Active/Acknowledged/PendingRecover/Recovered），新增 AlarmRule、AlarmGroupKey |

---

## 3. TaskChainEngine DAG 化

### 5 种 DAG 节点类型
| 节点 | 执行逻辑 |
|------|----------|
| ActionNode | 调用外部动作（PLC 写、API 调用、脚本） |
| WaitNode | 等待条件满足（Signal/Delay/External） |
| ParallelNode | 并行执行多个分支（WaitAll/WhenAny） |
| DelayNode | 等待指定时间 |
| DecisionNode | 条件表达式选择分支 |

### 新增文件
| 文件 | 说明 |
|------|------|
| `TaskEngine/Chain/TaskNode.cs` | 5 个 record 类型 + TaskGraph + NodeExecutionStatus |
| `TaskEngine/Chain/ChainBuilder.cs` | Fluent API（AddAction/AddWait/AddParallel/AddDelay/AddDecision + DependsOn + Build） |
| `TaskEngine/Chain/ChainExecutionEngine.cs` | DAG 执行器：拓扑排序（Kahn）+ retry + timeout + checkpoint |
| `TaskEngine/Chain/ChainRecoveryService.cs` | ConcurrentDictionary checkpoint，ResumeGraph 跳过已完成节点 |

### 修改文件
| 文件 | 改动 |
|------|------|
| `TaskEngine/Chain/TaskChainEngine.cs` | ITaskChainEngine 新增 ExecuteGraphAsync，注入 ChainExecutionEngine |

---

## 4. ObjectTrackingCenter 增强

### 新增索引
- **移动历史索引**：`ConcurrentDictionary<string, List<MovementRecord>>`，保留 1000 条/物体
- **空间索引**：`ConcurrentDictionary<string, HashSet<string>>`，Zone/Conveyor 双级
- **任务索引**：`ConcurrentDictionary<string, string>`，TaskId→ObjectId

### 新增文件
| 文件 | 说明 |
|------|------|
| `ObjectTracking/Models/Location.cs` | 层级位置（Zone→Conveyor→Position），FromString/PathKey |
| `ObjectTracking/Models/MovementRecord.cs` | 移动记录（From, To, MoveTime, TriggeredByTaskId, MovementType） |

### 修改文件
| 文件 | 改动 |
|------|------|
| `ObjectTracking/ObjectTrackingCenter.cs` | 新增空间/任务索引、移动历史、事件发布、GetObjectsByZone/GetMovementHistory/GetObjectByTask |

---

## 5. 跨模块集成与恢复

### 事件流
```
PLC 信号变化 → StateCenter.UpdateDeviceState → EventBus → AlarmCenter / TaskChainEngine
报警触发 → AlarmCenter.RaiseAlarmAsync → EventBus → StateCenter 同步报警状态
任务链执行 → ChainExecutionEngine → 每节点后 Checkpoint → EventBus 发布进度
物体移动 → ObjectTrackingCenter.MoveObject → EventBus → TaskChainEngine WaitNode 推进
```

### 多模块恢复
RecoveryManager 改为协调所有 ISnapshotProvider：
```
恢复顺序：StateCenter → ObjectTrackingCenter → AlarmCenter → TaskChainEngine（依赖反序）
保存顺序：StateCenter 统一收集
```

### 修改文件
| 文件 | 改动 |
|------|------|
| `Recovery/RecoveryManager.cs` | 注入 IEnumerable\<ISnapshotProvider\>，按 RestoreOrder 恢复 |
| `Application/DependencyInjection.cs` | 注册 StateCenter 同时作为 IStateCenter 和 ISnapshotProvider |

---

## 新增/修改文件统计

| 模块 | 新增 | 修改 |
|------|------|------|
| Common | 1 | 0 |
| StateCenter | 2 | 2 |
| AlarmCenter | 4 | 2 |
| TaskChainEngine | 4 | 1 |
| ObjectTrackingCenter | 2 | 1 |
| Recovery | 0 | 1 |
| Application | 0 | 1 |
| Host | 0 | 1 |
| **总计** | **13** | **9** |

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 所有 5 个项目（Core, Infrastructure, Application, Host, Desktop）编译通过
- 全链路：PLC 信号 → StateCenter diff → AlarmCenter 5 层管线 → EventBus → TaskChainEngine → ObjectTrackingCenter 事件串联

## 下一步建议
1. **持久化 AlarmRule 配置** — 从 appsettings.json 或数据库加载报警规则
2. **DecisionNode 委托注入** — 实现在 ChainExecutionEngine 中注册条件评估器
3. **WaitNode 事件驱动** — 使用 EventBus 订阅替代轮询
4. **ObjectTracking Center 恢复** — 实现 AlarmCenter / ObjectTrackingCenter 的 ISnapshotProvider
5. **批量报警历史查询** — 添加时间范围查询接口
