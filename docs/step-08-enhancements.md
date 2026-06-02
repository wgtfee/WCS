# Step 7 Enhancements: 5 项后续实现

## 背景

基于 Step 7 架构重构的"下一步建议"，全部 5 项一次性实现：

1. AlarmRule 持久化配置
2. DecisionNode 委托注入 + 条件分支路由
3. WaitNode 事件驱动（替代轮询）
4. ISnapshotProvider 多模块快照恢复
5. 批量报警历史查询接口

---

## Item 1: AlarmRule 持久化配置

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/Common/Options/WcsOptions.cs` | 新增 `List<AlarmRule> AlarmRules` 属性 |
| `src/Wcs.Host/appsettings.json` | 新增 `WcsOptions:AlarmRules` 配置段（DEADLOCK / PLC_COMM_LOST） |
| `src/Wcs.Host/Program.cs` | 启动时从配置加载 AlarmRule 并注入 AlarmCenter |

### 设计说明
- `SetAlarmRule(AlarmRule)` API 早已定义但从未被调用，所有报警走 `DefaultRule`（DelayRaiseMs=1000）
- 现在通过 `IOptions<WcsOptions>` 绑定配置，在 `Program.cs` 中遍历注册
- 未在配置中注册的 AlarmCode 仍使用 `DefaultRule`

---

## Item 2: DecisionNode 委托注入 + 条件分支路由

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/TaskEngine/Chain/ChainExecutionEngine.cs` | 委托注册表 + 分支路由 + prunedNodes 传递性剪枝 |

### 核心逻辑

```
DecisionNode 执行前:
  - 查 _decisionHandlers[Expression] 注册表
  - 有 handler → 执行委托，返回 true/false
  - 无 handler → 记录警告，默认返回 true

DecisionNode 执行后:
  - true  → chosenBranchId = TrueBranchNodeId, prunedNodes += FalseBranchNodeId
  - false → chosenBranchId = FalseBranchNodeId, prunedNodes += TrueBranchNodeId
  - 选中分支的根节点（所有依赖已完成/被剪枝）加入 readyQueue
  - 未选中分支整枝通过 prunedNodes 传递性剪枝
```

### 注册示例
```csharp
engine.RegisterDecisionHandler("x > 10", async (node, ct) =>
{
    var value = await someService.GetValueAsync();
    return value > 10;
});
```

---

## Item 3: WaitNode 事件驱动

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/TaskEngine/Chain/ChainExecutionEngine.cs` | WaitConditionHandlers 注册表 + EventBus 集成 + DeviceStateEventHandler |

### 核心逻辑

```
WaitNode 执行:
  - ConditionType == "Delay" → Task.Delay(node.PollMs)
  - 其他类型 → 查 _waitHandlers[ConditionType] 注册表
  - 内置 "Signal" handler: 通过 EventBus 订阅 DeviceStateChangedEvent
    - ConditionExpression 格式: "DeviceId:ExpectedStatus"
    - 使用 TaskCompletionSource<bool> + WaitAsync 超时
    - 事件匹配后 TrySetResult，清理订阅
```

### DeviceStateEventHandler
- 实现 `IEventHandler<DeviceStateChangedEvent>`
- 匹配 `DeviceId` 和 `NewStatus`（都是可选的，空字符串匹配任意）
- 单次触发后标记 `_handled = true`，防止重复设置

---

## Item 4: ISnapshotProvider 多模块快照恢复

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/ObjectTracking/ObjectTrackingCenter.cs` | 实现 ISnapshotProvider（ModuleName, CaptureSnapshotAsync, RestoreSnapshotAsync） |
| `src/Wcs.Core/AlarmCenter/AlarmCenter.cs` | 实现 ISnapshotProvider + AlarmCenterSnapshot DTO |
| `src/Wcs.Core/Recovery/RecoveryManager.cs` | ISnapshotRepository 改为 SystemSnapshot，SaveSnapshotAsync 调用所有 provider，RecoverAsync 按模块分发 |
| `src/Wcs.Application/DependencyInjection.cs` | 注册 ObjectTrackingCenter + AlarmCenter 为 ISnapshotProvider |
| `src/Wcs.Host/BackgroundServices/SnapshotBackgroundService.cs` | 改用 IRecoveryManager 实现多模块定时保存 |

### SystemSnapshot 格式

```json
{
  "Timestamp": "2026-06-02T10:00:00Z",
  "ModuleSnapshots": {
    "StateCenter": { "DeviceStates": {...}, "TaskRuntimes": {...}, ... },
    "ObjectTracking": { "object-001": { "CurrentPosition": "ZoneA", ... }, ... },
    "AlarmCenter": { "Alarms": [...], "Rules": {...} }
  }
}
```

### 恢复顺序
```
StateCenter → ObjectTracking → AlarmCenter → TaskChain
（依赖反序：先恢复基础数据，再恢复高级模块）
```

### 向后兼容
- 旧格式 `StateSnapshot` 文件会被自动检测并转换为 `SystemSnapshot`（仅包含 StateCenter 模块）
- 新格式区分：通过 JSON 根字段 `ModuleSnapshots` 存在性判断

### 各模块快照内容

| 模块 | CaptureSnapshotAsync 返回 | RestoreSnapshotAsync 处理 |
|------|--------------------------|--------------------------|
| StateCenter | `StateSnapshot`（5 种状态字典） | 清空并重建所有状态 |
| ObjectTracking | `Dictionary<string, ObjectState>` | 清空对象、历史、空间索引、任务索引，从快照重建 |
| AlarmCenter | `AlarmCenterSnapshot{Alarms, Rules}` | 清空报警和规则，折叠 PendingRaise→Active、PendingRecover→Recovered，重置防抖/风暴/聚合引擎 |

---

## Item 5: 批量报警历史查询

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/AlarmCenter/AlarmCenter.cs` | IAlarmCenter 接口 + AlarmCenter 实现新增 3 个查询方法 |

### 新增方法

```csharp
/// <summary>
/// 按时间范围查询报警历史（内存过滤）
/// </summary>
IEnumerable<AlarmState> GetAlarmsByTimeRange(DateTime from, DateTime to);

/// <summary>
/// 按代码+时间范围查询
/// </summary>
IEnumerable<AlarmState> GetAlarmsByCode(string alarmCode, DateTime from, DateTime to);

/// <summary>
/// 获取报警总数（含已恢复）
/// </summary>
int GetTotalCount();
```

### 实现说明
- 基于内存 `ConcurrentDictionary<string, AlarmState> _alarms` 的 LINQ 过滤
- `OccurTime` 作为时间范围筛选字段
- 所有查询返回 `ToList()` 快照，避免迭代时并发修改风险

---

## 修改文件清单

| 文件 | Item | 改动 |
|------|------|------|
| `Common/Options/WcsOptions.cs` | 1 | 新增 `AlarmRules` 属性 |
| `Wcs.Host/appsettings.json` | 1 | 新增 `AlarmRules` 配置段 |
| `Wcs.Host/Program.cs` | 1 | 启动时加载并注入 AlarmRule |
| `TaskEngine/Chain/ChainExecutionEngine.cs` | 2,3 | DecisionNode 分支路由 + WaitNode 事件驱动 |
| `ObjectTracking/ObjectTrackingCenter.cs` | 4 | 实现 ISnapshotProvider |
| `AlarmCenter/AlarmCenter.cs` | 4,5 | 实现 ISnapshotProvider + 批量查询方法 |
| `Recovery/RecoveryManager.cs` | 4 | SystemSnapshot 多模块保存/恢复 + 向后兼容 |
| `Application/DependencyInjection.cs` | 4 | 注册 ISnapshotProvider |
| `Host/BackgroundServices/SnapshotBackgroundService.cs` | 4 | 改用 IRecoveryManager |

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 个项目编译通过
- Item 4: 旧格式 `StateSnapshot` 自动转换为 `SystemSnapshot`，保持向后兼容
- Item 5: 新增查询方法直接基于内存 `_alarms` 过滤
