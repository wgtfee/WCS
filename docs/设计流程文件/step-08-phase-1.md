# Step 8 — Phase 1: Core Upgrades

## 背景

Phase 1 为三轨并行的核心升级，涵盖资源锁、空间拓扑、任务链版本管理三大模块。

---

## Track A: ResourceLockManager Lease/TTL

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/ResourceLock/ResourceLockManager.cs` | 完整重构：新增 TTL/Lease、异步 API、后台自动清理 |

### IResourceLockManager 接口变更

新增方法：
```csharp
Task<LockAcquireResult> TryAcquireAsync(string resourceId, string ownerId,
    TimeSpan? ttl = null, CancellationToken ct = default);
bool RenewLease(string resourceId, string ownerId, string leaseToken, TimeSpan extension);
TimeSpan? GetRemainingTtl(string resourceId);
void ForceRelease(string resourceId);
Dictionary<string, string> GetAllLocks();
IEnumerable<string> GetLocksByOwner(string ownerId);
int CleanupExpiredLocks(TimeSpan maxAge);
```

### LockAcquireResult
```csharp
public class LockAcquireResult
{
    public bool Success { get; set; }
    public string? LeaseToken { get; set; }
    public string? OwnerId { get; set; }
    public DateTime? ExpiryTime { get; set; }
    public string? FailureReason { get; set; }
}
```

### 关键设计
- **LockEntry** 新增 `Ttl`、`ExpiryTime`、`LastHeartbeat`、`LeaseToken` 字段
- **TryAcquireAsync**：TryAdd → 检查过期 → TryUpdate → 返回结果含 FailureReason
- **RenewLease**：验证 ownerId + leaseToken，更新 ExpiryTime
- **Timer 5s 间隔后台清理**：自动移除 ExpiryTime 已过的锁
- 同步 `TryAcquire(string, string, int)` 保持旧签名 100% 向后兼容
- 无 TTL 的锁（无限期）不会被后台清理

---

## Track B: ObjectTracking 空间拓扑

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/ObjectTracking/Topology/Zone.cs` | 新建 — Zone record |
| `src/Wcs.Core/ObjectTracking/Topology/Node.cs` | 新建 — NodeType 枚举 + Node record |
| `src/Wcs.Core/ObjectTracking/Topology/Edge.cs` | 新建 — EdgeCapability 枚举 + Edge record |
| `src/Wcs.Core/ObjectTracking/Topology/TopologyGraph.cs` | 新建 — 线程安全拓扑图 |
| `src/Wcs.Core/ObjectTracking/Models/Location.cs` | 新增 `NodeId` 属性 + `ResolveNodeId()` |
| `src/Wcs.Core/ObjectTracking/ObjectTrackingCenter.cs` | 集成 TopologyGraph（可选） |

### 模型定义

```csharp
public enum NodeType { TransferPoint, Buffer, Junction, DivergePoint, EntryPoint, ExitPoint }

[Flags]
public enum EdgeCapability { None = 0, Transport = 1, Transfer = 2, Both = Transport | Transfer }

public record Zone { string ZoneId, string DisplayName, Dictionary<string,string>? Properties }
public record Node { string NodeId, string ZoneId, ConveyorId, PositionId, NodeType Type, ... }
public record Edge { string EdgeId, FromNodeId, ToNodeId, Weight=1, IsOccupied, EdgeCapability }
```

### TopologyGraph 功能
- **CRUD**：AddNode/RemoveNode/HasNode（自动维护邻接表）；AddZone 级联删除节点和边
- **BFS 最短路径**：`GetShortestPath(from, to, capability?, avoidOccupied?)` 支持边权重、能力过滤、避开占用
- **DFS 所有路径**：`GetAllPaths(from, to, maxResults, maxDepth)` 防爆限深
- **可达性**：`GetReachableNodes`（BFS 遍历）、`IsReachable`
- **占用管理**：`MarkEdgeOccupied`（乐观并发重试）、`GetOccupiedEdges`
- **快照**：`TopologySnapshot` record + `GetSnapshot` / `RestoreFromSnapshot`
- **辅助**：`ValidatePath`（验证节点序列连通性）、`GetStats`、`GetZoneNodes/Edges`

### 线程安全
- `ConcurrentDictionary` 存储 Zones/Nodes/Edges
- `ConcurrentDictionary` 邻接表 + `lock(_adjacencyLock)` 保护集合操作
- `MarkEdgeOccupied` 使用 `TryUpdate` 乐观循环

### 向后兼容
- ObjectTrackingCenter.TopologyGraph 为 nullable，默认 null
- 不设置拓扑图时所有现有功能不变
- Location 新增 NodeId 为 optional

---

## Track C: TaskChain 版本管理

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/TaskEngine/Chain/TaskChainDefinition.cs` | 新建 — 版本化链定义 |
| `src/Wcs.Core/TaskEngine/Chain/TaskNode.cs` | TaskGraph 新增 `Version` + `DefinitionId` |
| `src/Wcs.Core/TaskEngine/Chain/ChainBuilder.cs` | 新增 `WithDefinition(TaskChainDefinition)` |
| `src/Wcs.Core/TaskEngine/Chain/TaskChainEngine.cs` | 新增版本注册表 + 兼容检查 |

### TaskChainDefinition
```csharp
public class Version { int Major, Minor }  // 序列化友好的 semver

public class TaskChainDefinition
{
    string DefinitionId;       // GUID
    string Name;               // 业务名称（如 "TransferChain-V2"）
    Version Version;           // 语义版本号
    string Description;
    DateTime CreatedTime;
    DateTime? LastModified;
    bool IsBreakingChange;     // 是否破坏性变更
    TaskGraph? Graph;          // 关联的 DAG 图
}
```

### 版本兼容机制
- `TaskChainEngine.RegisterDefinition(definition)` 注册链定义到 `ConcurrentDictionary`
- `ExecuteGraphAsync` 执行时自动检查版本：如果图定义 Major 版本落后于注册定义，记录警告
- `TaskGraph` 携带 `DefinitionId` 和 `Version`，与定义双向关联

### 使用示例
```csharp
var def = new TaskChainDefinition
{
    Name = "TransferChain",
    Version = new Version(2, 0),
    IsBreakingChange = true
};

var graph = ChainBuilder.Create()
    .AddAction("a1", "PlcWrite")
    .AddWait("w1", new WaitCondition { DeviceId = "CV01", ExpectedStatus = "Ready" })
    .WithDefinition(def)
    .Build();

engine.RegisterDefinition(def);
await engine.ExecuteGraphAsync(graph);
```

### 向后兼容
- TaskGraph.Version / DefinitionId 都是 nullable，不设置不影响原有执行路径
- TaskChainDefinition 完全独立，不侵入旧执行流
- 版本检查只会 log warning，不会阻断执行

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 项目编译通过
- 所有新增字段有默认值/nullable，向后兼容
- Phase 1 三轨全部完成
