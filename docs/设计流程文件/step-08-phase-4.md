# Step 8 — Phase 4: DecisionHandler 语义命名

## 背景

Phase 4 为 DecisionNode 引入业务语义命名约定，使 DAG 定义更具可读性和可维护性。

---

## Item 7: 按业务语义命名约定

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/TaskEngine/Chain/ChainBuilder.cs` | AddDecision XML 文档更新，推荐语义命名 |

### 语义命名约定

决策节点的 `expression` 参数使用业务语义名称而非实现表达式：

```csharp
// 推荐：语义命名（业务含义清晰）
builder.AddDecision("d1", "CheckStorageAvailable", "branch-full", "branch-empty");

// 传统：实现表达式（不推荐）
builder.AddDecision("d1", "storage.available > 0", "branch-full", "branch-empty");
```

### 运行时匹配
```csharp
// 按语义名称注册处理器
engine.RegisterDecisionHandler("CheckStorageAvailable", async (node, ct) =>
{
    var available = await CheckStorageAsync();
    return available;
});
```

### 向后兼容
- 无代码破坏性变更
- 旧 Expression 方式完全兼容
- 语义名称只是新约定，可以混合使用

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 项目编译通过
