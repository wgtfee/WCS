# Step 8 — Phase 2: EventBus 持久化

## 背景

Phase 2 为 EventBus 增加可选的持久化层：事件写入文件系统，系统恢复后重放快照之后的事件。

---

## Item 3: EventStore + Event Replay

### 改动文件
| 文件 | 改动 |
|------|------|
| `src/Wcs.Core/EventBus/Persistence/IEventStore.cs` | 新建 — 事件存储接口 |
| `src/Wcs.Core/EventBus/Persistence/FileEventStore.cs` | 新建 — 基于文件系统的 JSON-lines 实现 |
| `src/Wcs.Core/EventBus/Persistence/EventReplayService.cs` | 新建 — 快照后事件重放服务 |
| `src/Wcs.Core/EventBus/Publisher/EventBus.cs` | EventBus 可选集成 IEventStore |
| `src/Wcs.Core/Recovery/RecoveryManager.cs` | 恢复后自动触发事件重放 |
| `src/Wcs.Application/DependencyInjection.cs` | 注册 EventStore 相关服务 |

### IEventStore 接口
```csharp
public interface IEventStore
{
    Task AppendAsync(IEvent @event, CancellationToken ct = default);
    Task<IReadOnlyList<IEvent>> QueryAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<IEvent>> GetLatestAsync(int count, CancellationToken ct = default);
    Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken ct = default);
}
```

### FileEventStore
- **格式**：JSON-lines（每行一个 JSON 对象），按小时轮转文件 `events_yyyyMMddHH.jsonl`
- **写入**：ConcurrentQueue 缓冲 + Timer 3s 间隔刷盘 + 批量满 100 条立即触发
- **查询**：自动 flush 待写入数据后读取文件，按文件时间范围过滤
- **反序列化**：每条记录存储事件类型程序集限定名 + JsonElement payload
- **线程安全**：ConcurrentQueue 入队，SemaphoreSlim 保护文件写操作
- **生命周期**：IDisposable，Dispose 时同步刷空缓冲区

### EventReplayService
- **功能**：读取快照时间戳之后的事件，筛选出"可重放"事件类型，按时间顺序重新发布到 EventBus
- **可重放事件白名单**（影响系统状态）：

| 事件 | 原因 |
|------|------|
| DeviceStateChangedEvent | 设备状态是系统核心状态 |
| TaskStateChangedEvent | 任务状态驱动流程引擎 |
| ObjectLocationChangedEvent | 物体位置用于路径规划 |
| PlcBlockChangedEvent | PLC 数据反映设备状态 |

- **不可重放事件**：AlarmRaisedEvent、AlarmRecoveredEvent 等衍生事件（由状态变化重新触发）
- `RegisterReplayableType<T>()` 方法允许扩展白名单

### EventBus 集成
- EventBus 构造函数接收可选的 `IEventStore?` 参数
- `PublishAsync<T>` 中 handler 执行完毕后，通过 `Task.Run` fire-and-forget 持久化事件
- 持久化失败不影响主事件发布流程
- 无 EventStore 配置时行为不变

### RecoveryManager 集成
- RecoveryManager 构造函数接收可选的 `EventReplayService?` 参数
- `RecoverAsync` 成功恢复所有模块后，自动调用 `EventReplayService.ReplayAsync(snapshotTimestamp)`
- 重放结果包含在 `RecoveryResult.Message` 中

### EventRecord 格式
```json
{
  "eventId": "abc123",
  "eventType": "Wcs.Core.EventBus.Events.DeviceStateChangedEvent, Wcs.Core",
  "occurTime": "2026-06-02T10:30:00Z",
  "priority": 2,
  "source": "DeviceStateChangedEvent",
  "payload": { /* 原始事件字段 */ }
}
```

---

## 验证结果
- `dotnet build` — 0 errors, 2 pre-existing warnings
- 全 5 项目编译通过
- 所有新增功能可选（null = 不启用），完全向后兼容
