# Step 8 — Phase 0: Quick Wins

## 背景

Step8 共 6 个 Phase，Phase 0 为无外部依赖的快速胜出项，包含 3 个独立增强。

---

## Item 6: AlarmRule 配置格式升级

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/StateCenter/Models/StateModels.cs` | AlarmRule 新增 `SuppressWindowMs` + `AutoRecover` |
| `src/Wcs.Host/appsettings.json` | 更新 AlarmRules 配置，加入新字段 |

### AlarmRule 新增属性

```csharp
public bool AutoRecover { get; set; } = false;   // 条件恢复时自动清除，无需人工确认
public int SuppressWindowMs { get; set; } = 0;    // 抑制窗口（毫秒），0=不抑制
```

### 配置示例
```json
{
  "AlarmCode": "DEADLOCK",
  "DelayRaiseMs": 1000,
  "DelayRecoverMs": 3000,
  "AutoRecover": true,
  "SuppressWindowMs": 60000,
  "AlarmGroup": "System"
}
```

---

## Item 9: ISnapshotProvider.RestoreOrder

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/Common/Interfaces/ISnapshotProvider.cs` | 接口新增 `int RestoreOrder { get; }` |
| `src/Wcs.Core/StateCenter/Implementation/StateCenter.cs` | 显式实现 `RestoreOrder => 0`；修复 RestoreSnapshotAsync 支持 JsonElement |
| `src/Wcs.Core/ObjectTracking/ObjectTrackingCenter.cs` | `RestoreOrder => 1` |
| `src/Wcs.Core/AlarmCenter/AlarmCenter.cs` | `RestoreOrder => 2` |
| `src/Wcs.Core/Recovery/RecoveryManager.cs` | 移除硬编码 `RestoreOrder` 数组，改为 `_providers.OrderBy(p => p.RestoreOrder)` |

### 设计说明
- 以前：RecoveryManager 硬编码 `string[] RestoreOrder = { "StateCenter", "ObjectTracking", "AlarmCenter", "TaskChain" }`
- 现在：每个 ISnapshotProvider 自声明 `RestoreOrder`，RecoveryManager 统一 `OrderBy`
- 新增模块（如未来 RouteEngine）只需注册 ISnapshotProvider 并设置 RestoreOrder，无需修改 RecoveryManager
- StateCenter.RestoreSnapshotAsync 同时支持 `JsonElement`（新格式）和 `StateSnapshot`（旧格式）

---

## Item 8: WaitNode 结构化条件

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/TaskEngine/Chain/TaskNode.cs` | 新增 `WaitCondition` record；WaitNode 新增 `Condition` 属性 |
| `src/Wcs.Core/TaskEngine/Chain/ChainBuilder.cs` | 新增 `AddWait(nodeId, WaitCondition)` 重载 |
| `src/Wcs.Core/TaskEngine/Chain/ChainExecutionEngine.cs` | ExecuteWaitSignalAsync 优先使用结构化 Condition |

### WaitCondition 定义
```csharp
public record WaitCondition
{
    public string DeviceId { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string? SignalName { get; init; }
}
```

### 使用示例
```csharp
// 旧方式（字符串解析）
ChainBuilder.Create()
    .AddWait("wait-1", "Signal", "CV01:Ready")

// 新方式（结构化）
ChainBuilder.Create()
    .AddWait("wait-1", new WaitCondition { DeviceId = "CV01", ExpectedStatus = "Ready" })
```

### 向后兼容
- `Condition` 优先于 `ConditionExpression`
- `Condition` 为 null 时回退到旧字符串解析路径
- 所有旧代码无需修改

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 项目编译通过
- 所有新增字段有默认值，向后兼容
