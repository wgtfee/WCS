# Step 8 — Phase 3: AlarmCenter 根因分析

## 背景

Phase 3 为 AlarmCenter 增加树形根因层次分析能力，在已有 flat 分组（Device + AlarmGroup）基础上叠加父子报警树结构。

---

## Item 5: 树形根因层次

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/StateCenter/Models/StateModels.cs` | AlarmState 新增 `RootCauseAlarmId` + `RootCauseDepth` |
| `src/Wcs.Core/AlarmCenter/Engine/AlarmAggregationEngine.cs` | 新增树层次结构 + 路径/子树/递归恢复方法 |
| `src/Wcs.Core/AlarmCenter/AlarmCenter.cs` | IAlarmCenter 新增根因查询方法 + 实现 |

### AlarmState 新增属性
```csharp
public string? RootCauseAlarmId { get; set; }  // 根因报警 ID（根因树）
public int RootCauseDepth { get; set; }         // 根因树深度（根因=0）
```

### AlarmAggregationEngine 树层次

新增数据结构：
```csharp
ConcurrentDictionary<string, string?> _parentMap;      // alarmId → parentAlarmId (null=根)
ConcurrentDictionary<string, HashSet<string>> _childrenMap; // parentId → childIds
ConcurrentDictionary<string, int> _depthMap;            // alarmId → depth
```

新增方法：

| 方法 | 说明 |
|------|------|
| `RegisterAlarmHierarchy(alarmId, parentAlarmId)` | 注册父子关系+自动计算深度 |
| `GetRootCausePath(alarmId)` | 从当前报警向上遍历到根因，返回路径列表 |
| `GetDescendantAlarms(alarmId)` | BFS 递归获取所有后代 |
| `GetRootCauseDepth(alarmId)` | 报警在树中的深度 |
| `RecoverTree(rootAlarmId)` | 递归恢复整个子树（叶子→根清理索引） |

### IAlarmCenter 新增方法

```csharp
IReadOnlyList<string> GetRootCausePath(string alarmId);
IEnumerable<AlarmState> GetDeviceRootAlarms(string deviceId);
int GetRootCauseDepth(string alarmId);
```

### 使用示例
```csharp
// 注册层次关系
aggregation.RegisterAlarmHierarchy("ALM-PLC-001", parentId: "ALM-POWER-001");
aggregation.RegisterAlarmHierarchy("ALM-PLC-002", parentId: "ALM-POWER-001");
aggregation.RegisterAlarmHierarchy("ALM-POWER-001", parentId: null); // 根因

// 查询根因路径
var path = alarmCenter.GetRootCausePath("ALM-PLC-001");
// → ["ALM-PLC-001", "ALM-POWER-001"]

// 获取报警深度
var depth = alarmCenter.GetRootCauseDepth("ALM-PLC-001"); // → 1
var rootDepth = alarmCenter.GetRootCauseDepth("ALM-POWER-001"); // → 0

// 递归恢复子树
aggregation.RecoverTree("ALM-POWER-001");
// → 恢复 POWER-001 + PLC-001 + PLC-002
```

### 向后兼容
- AlarmState 新增字段 nullable / default=0，不影响现有序列化
- 树层次独立于 flat 分组，两者可共存
- 不调用 RegisterAlarmHierarchy 则所有新功能返回空结果

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 项目编译通过
- 所有新字段有默认值，向后兼容
