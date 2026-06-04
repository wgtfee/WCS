# V8: Production Hardening — 可恢复性、可靠性、审计性

> V8 不加新功能，只处理 7 个真正决定系统能否连续运行 365×24 小时的 Production Hardening 问题。
>
> 核心主题：**一致性、可靠性、恢复性、审计性、可运维性**

---

## V8 7 项变更

| # | 变更 | 文件 | 类型 | 解决什么问题 |
|---|------|------|------|-------------|
| 1 | **ITaskQueueStore** | `TaskEngine/QueueStore/` | 可恢复性 | 崩溃后调度队列丢失 |
| 2 | **CommandCenter PLC ACK** | `CommandCenter/` | 可靠性 | 写PLC ≠ 设备执行 |
| 3 | **StateRetentionPolicy** | `StateCenter/Models/` | 可运维性 | StateCenter 无限增长 |
| 4 | **WaitNode Subscribe-Then-Check** | `ChainExecutionEngine` | 可靠性 | Check-Then-Subscribe 竞态 |
| 5 | **Signal 幂等窗口** | `TaskGenerator` | 可靠性 | PLC 信号毛刺重复触发 |
| 6 | **Reservation TTL** | `ObjectTrackingCenter` | 可恢复性 | 设备故障后预占位永不释放 |
| 7 | **TraceCenter** | `TraceCenter/` | 审计性 | 任务/命令/运输执行轨迹 |

---

## 1. ITaskQueueStore — 持久化调度队列

### 问题

```
WCS-A 运行中:  TaskQueue [T1, T2, T3]
WCS-A 崩溃:    TaskQueue 全部丢失
WCS-A 重启:    StateCenter 恢复了，Checkpoint 恢复了
               ✓ TaskContext ✓ 状态 ✓ 报警
               ✗ Scheduler.Queue = empty
               → T1 可能恢复，T2、T3 永远丢失
```

### 解决

```csharp
public interface ITaskQueueStore
{
    Task EnqueueAsync(TaskContext task);          // 入队时持久化
    Task RemoveAsync(string taskId);              // 出队时移除
    Task<List<TaskContext>> GetPendingTasksAsync(); // 恢复时读取
}
```

**恢复流程：**
```
RecoveryManager
  ↓
ITaskQueueStore.GetPendingTasksAsync()
  ↓
TaskScheduler.RecoverPendingTasksAsync()
  ↓
队列恢复 → 继续调度
```

V8 提供 `InMemoryTaskQueueStore` 实现（生产环境应替换为 Redis/DB）。

---

## 2. CommandCenter PLC ACK 模型

### 问题

V7 状态机：`Sent → Accepted → Executing → Completed`

但 PLC 世界实际流程：

```
PC 写命令位
    ↓ (PLC 扫描周期)
PLC 看到命令
    ↓ (PLC 置 ACK 位)
PC 读到 ACK → 确认设备收到     ← 以前没这个
    ↓ (PLC 执行)
PLC 置 DONE 位
PC 读到 DONE → 确认执行完成    ← 以前没这个
    ↓ (PC 清命令位)
PLC 清 ACK
```

### 解决

V8 状态机：
```
Sent → Acked → Executing → Done → Completed
                              ↑      ↑
                          PLC DONE  WCS 确认
```

新增方法：
- `ConfirmAcked(commandId)` — PLC 确认收到命令
- `ConfirmDone(commandId)` — PLC 完成执行

---

## 3. StateRetentionPolicy — 状态保留策略

### 问题

StateCenter 使用 `ConcurrentDictionary` 存储所有状态。随着时间推移：

- 完成任务永远不会被清理
- 已恢复报警永远在内存
- 物体移动历史无限增长

→ 半年后 StateCenter ≈ 内存数据库

### 解决

```csharp
public class StateRetentionPolicy
{
    TimeSpan CompletedTaskRetention = 24h;   // 完成任务保留 24 小时
    TimeSpan RecoveredAlarmRetention = 7d;   // 已恢复报警保留 7 天
    int MaxObjectHistoryPerObject = 1000;     // 每个物体最大历史 1000 条
    TimeSpan FailedCommandRetention = 48h;    // 失败命令保留 48 小时
}
```

后台 `StateCleanupService` 定时执行清理。

---

## 4. WaitNode Subscribe-Then-Check

### 问题

V3 的 State+Event 双保险已经很好，但仍有竞态窗口：

```
T1: WaitNode 开始等待
T2: 读取 State = NotReady        ← 检查
T3: Ready 事件发生                ← 事件到来
T4: 订阅 EventBus                ← 订阅
T5: 永远等不到                       ← 竞态！
```

这叫 **Check-Then-Subscribe** 竞态窗口。

### 解决

改成 **Subscribe-Then-Check**：

```
T1: WaitNode 开始等待
T2: 订阅 EventBus                 ← 先订阅
T3: 读取 State = NotReady         ← 后检查（此时已订阅，不会丢失 Ready 事件）
    如果 State = Ready → 直接返回 success
    如果 State = NotReady → await 事件
```

**先订阅、后检查**，消除竞态窗口。

---

## 5. Signal 幂等窗口

### 问题

工业现场，同一个 PLC 信号可能连续多次触发：

- PLC 信号毛刺
- SignalMapper 重复发布
- 网络抖动导致重传

→ RuleEngine 在 5ms 内生成 3 个相同的 Task

### 解决

```csharp
// TaskGenerator 中维护幂等窗口
private readonly TimeSpan _idempotencyWindow = TimeSpan.FromSeconds(5);
private readonly ConcurrentDictionary<string, DateTime> _idempotencyCache = new();

// 5 秒内相同信号只处理一次
if (IsDuplicateSignal(signalEvent))
    return; // 忽略
```

---

## 6. Reservation TTL — 预占位超时

### 问题

```
托盘 A 预占 CV03
    ↓
设备故障
    ↓
任务取消
    ↓
CV03 的 ReservedNodeId 永远不释放
    ↓
后续所有需要经过 CV03 的路径规划都失败
```

### 解决

```csharp
/// 清理超过 5 分钟的预占位（自动释放）
public int CleanupExpiredReservations(TimeSpan? ttl = null)
{
    ttl ??= TimeSpan.FromMinutes(5);
    // 遍历所有物体，ReservedNodeId != null 且 UpdateTime < cutoff
    // → 释放预占位
}
```

类似 `ResourceLock` 的 TTL 机制，防止设备故障或任务取消后预占位永不释放。

---

## 7. TraceCenter — 执行轨迹中心

### 问题

现场最常问的问题：
> "这个托盘为什么没进库？"
> "这个任务为什么跑了 20 分钟？"
> "这个命令 PLC 有没有收到？"

没有单一地方可以回答这些问题。

### 解决

```
TraceCenter（纯 WCS 执行轨迹，非业务流程审计）

Task Trace:
Task T001: 09:01:01 Created → 09:01:03 Scheduled → 09:01:05 Running
           → 09:01:10 Wait CV01 → 09:01:20 Satisfied → 09:01:22 Completed

Command Trace:
Command C888: 09:01:03 Sent → 09:01:04 Acked → 09:01:06 Executing
              → 09:01:20 Done → 09:01:21 Completed

Device Trace:
CV01: 09:01:00 Running → 09:01:10 Idle → 09:01:20 Running → 09:01:22 Idle
```

### 使用示例

```csharp
// TaskOrchestrator 中记录
traceCenter.TraceQuick(taskId, TraceEventType.TaskCreated, "Task created");
traceCenter.TraceQuick(taskId, TraceEventType.TaskRunning, "Task started", deviceId);

// CommandCenter 中记录
traceCenter.TraceQuick(commandId, TraceEventType.CommandSent, "Command sent", deviceId);
traceCenter.TraceQuick(commandId, TraceEventType.CommandAcked, "PLC acknowledged", deviceId);

// ObjectTrackingCenter 中记录
traceCenter.TraceQuick(objectId, TraceEventType.NodeArrived, "Arrived at " + nodeId, null, nodeId);

// 排查问题
var taskTrace = traceCenter.GetTrace("T001");
// 输出:
// 09:01:01 Created
// 09:01:03 Scheduled (2s)
// 09:01:05 Running (2s)
// 09:01:10 Wait CV01 (5s)  ← 这里卡了 5 秒
// 09:01:20 Satisfied (10s)  ← 等了 10 秒才满足
// 09:01:22 Completed (2s)
```

---

## V8 变更清单

### 新增

| 文件 | 说明 |
|------|------|
| `TaskEngine/QueueStore/ITaskQueueStore.cs` | 持久化队列接口 |
| `TaskEngine/QueueStore/InMemoryTaskQueueStore.cs` | 内存实现 |
| `TraceCenter/TraceCenter.cs` | 执行轨迹中心 |
| `StateCenter/Models/StateModels.cs` | 新增 `StateRetentionPolicy` 类 |

### 修改

| 文件 | 变更 |
|------|------|
| `TaskEngine/Scheduler/TaskScheduler.cs` | +ITaskQueueStore 集成，+RecoverPendingTasksAsync |
| `CommandCenter/CommandModels.cs` | 状态机: Acked/Done，+ConfirmAcked/ConfirmDone |
| `CommandCenter/CommandCenter.cs` | +ConfirmAcked/ConfirmDone 实现 |
| `TaskEngine/Chain/ChainExecutionEngine.cs` | WaitNode: Subscribe-Then-Check |
| `RuleEngine/TaskGenerator.cs` | +5s 幂等窗口去重 |
| `ObjectTracking/ObjectTrackingCenter.cs` | +CleanupExpiredReservations |

---

## 验证

- `dotnet build` — **0 errors**
- `dotnet test` — **108/108 全部通过**

---

## 一句话总结 V8

```
调度队列不丢 + PLC 双向握手机 + 状态自动清理 +
等待零竞态 + 信号幂等 + 预占位超时 + 全链路轨迹追踪
= 365×24 小时可运行的纯 WCS Runtime Platform
```
