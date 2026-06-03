# V7: 工业级可观测性 — CommandCenter + DeadLetterCenter + MetricsCenter

> 基于 V6 纯 WCS 架构的工业级稳定性补完。
> 不加新的业务功能，只补生产环境必备的三大基础设施。

---

## V7 新增模块

| # | 模块 | 文件 | 生产环境价值 |
|---|------|------|-------------|
| 1 | **CommandCenter** | `CommandCenter/` | 命令发送≠设备执行，追踪完整生命周期 |
| 2 | **DeadLetterCenter** | `DeadLetterCenter/` | 失败记录统一管理，线上可排查 |
| 3 | **MetricsCenter** | `MetricsCenter/` | 统一指标，对接 Prometheus/Grafana |
| 4 | **AlarmBus** | `EventBus/Publisher/AlarmBus.cs` | 报警事件独立通道 |

---

## 1. CommandCenter — 命令中心

### 解决的问题

目前：ActionNode 写 PLC 成功 = 「执行成功」
实际上：写 PLC 成功 ≠ 设备执行完成

```
写 PLC 成功      ← 这个 V1 就能判断
设备收到命令     ← 这个需要 Accepted 确认
设备开始执行     ← 这个需要 Executing 确认
设备执行完成     ← 这个需要 Completed 确认
```

### 命令状态机

```
Created
  ↓ SendCommand()
Sent
  ↓ 设备确认收到
Accepted
  ↓ 设备开始执行
Executing
  ↓ 执行完成
Completed

异常路径：
Sent → Timeout（超时未收到确认）
Accepted → Rejected（设备拒绝执行）
Executing → Failed（执行失败）
任意状态 → Cancelled（取消）
```

### 使用示例

```csharp
// ActionNode 中替代直接写 PLC
var cmd = await commandCenter.SendCommandAsync(
    deviceId: "CV01",
    commandType: "StartConveyor",
    payload: "{\"speed\":1500}",
    taskId: task.TaskId,
    timeoutMs: 5000
);

// 等待设备响应（通过 EventBus 或 PLC 回读）
// 收到确认信号后：
commandCenter.ConfirmAccepted(cmd.CommandId);

// 收到执行中信号：
commandCenter.ConfirmExecuting(cmd.CommandId);

// 收到完成信号：
commandCenter.ConfirmCompleted(cmd.CommandId);

// 超时自动检测（Timer 驱动，无需手动调用）
var timeoutCmds = commandCenter.GetTimeoutCommands();
```

### 关键价值

- **命令审计**：每条命令何时发送、何时完成、耗时多少
- **超时自动发现**：不再等用户反馈才发现命令没执行
- **命令可重复**：可区分「从未发送」和「发送了但没响应」

---

## 2. DeadLetterCenter — 死信中心

### 解决的问题

目前：异常 throw 或 log 后无人追踪，线上排查靠翻日志。

解决：所有失败记录集中管理，可查询、可统计、可标记处理。

### 死信类型

| 类型 | 说明 | 来源 |
|------|------|------|
| `TaskGenerationFailed` | 规则引擎生成任务失败 | RuleEngine |
| `TaskExecutionFailed` | 任务执行失败 | ChainExecutionEngine |
| `CommandTimeout` | 命令超时 | CommandCenter |
| `CommandRejected` | 命令被设备拒绝 | CommandCenter |
| `DeviceFault` | 设备故障 | DeviceHealthMonitor |
| `RouteFailed` | 路由规划失败 | TransportRouteCenter |
| `RuleEngineException` | 规则匹配异常 | RuleEngine |
| `UnhandledException` | 未处理异常 | 全局 |

### 使用示例

```csharp
// 任务执行失败时投递死信
deadLetter.PostQuick(
    type: DeadLetterType.TaskExecutionFailed,
    sourceModule: "ChainExecutionEngine",
    summary: $"Task {taskId} failed after {retryCount} retries",
    originalId: taskId,
    deviceId: deviceId,
    detail: errorMessage
);

// 线上排查：查某个设备的所有失败
var deviceFails = deadLetter.Query(deviceId: "CV01");

// 查未处理的死信
var unresolved = deadLetter.GetUnresolvedCount();

// 人工处理后标记
deadLetter.Resolve(id, "Operator张三: 已重启设备");
```

---

## 3. MetricsCenter — 指标中心

### 解决的问

目前：系统运行状态只能通过 log 和 Event 感知，没有量化指标。

解决：统一指标收集，未来可对接 Prometheus/Grafana。

### 预注册指标

| 指标名 | 类型 | 说明 |
|--------|------|------|
| `task.tps` | Gauge | 任务每秒处理量 |
| `task.completed` | Counter | 完成任务数 |
| `task.failed` | Counter | 失败任务数 |
| `task.queue_depth` | Gauge | 当前队列深度 |
| `plc.read_latency_ms` | Histogram | PLC 读取延迟分布 |
| `alarm.active` | Gauge | 当前活跃报警数 |
| `device.active` | Gauge | 当前活跃设备数 |
| `command.timeout` | Counter | 命令超时累计数 |
| `route.calculated` | Counter | 路由计算次数 |

### 使用示例

```csharp
// 在 PlcPollingService 中使用
using var _ = metrics.MeasureDuration("plc.read_latency_ms");
var data = await connection.ReadBlockAsync(blockNumber, length, ct);

// 在 TaskScheduler 中使用
metrics.Record("task.queue_depth", _queue.Count);
metrics.Increment("task.completed");

// 获取所有指标快照（用于监控面板）
var snapshot = metrics.GetSnapshot();
```

---

## 4. EventBus 三分区

目前：EventBus + SignalBus 两分区。

V7 升级为三分区：

```
SignalBus（PLC 信号事件）
├── PlcBlockChangedEvent
├── ConveyorReadyChangedEvent
├── PalletArrivedEvent
└── DeviceFaultEvent

DomainBus（标准业务事件）
├── TaskStateChangedEvent
├── DeviceStateChangedEvent
└── ObjectLocationChangedEvent

AlarmBus（报警事件）★ V7 新增
├── AlarmRaisedEvent
├── AlarmRecoveredEvent
├── AlarmEscalatedEvent
└── EmergencyStopEvent
```

**隔离效果：** 报警风暴不影响业务事件，业务事件不干扰 PLC 信号处理。

---

## 架构演进总览

```
V1:  Demo
V2:  Step8 工业级增强（5项）
V3:  架构审计整改（SignalMapper + StateCenter解耦 + FenceToken + ...）
V4:  性能+解耦（CRC32 + SignalBus + RuleEngine + TaskGenerator）
V5:  WCS 扩展（RouteCenter + WorkflowCenter + DeviceCapability + AlarmEscalation）
     └── 审查发现 WMS 渗透
V6:  纯 WCS 净化（删除 WorkflowCenter，缩减 RouteCenter/DeviceCapability）
V7:  工业级可观测性（CommandCenter + DeadLetterCenter + MetricsCenter + AlarmBus）
     └── 不加业务，只补稳定性基础设施
```

---

## 一句话总结 V7

```
写 PLC 成功 ≠ 设备执行成功 → CommandCenter 追踪完整生命周期
任务失败不可追溯 → DeadLetterCenter 集中管理
系统状态不可见 → MetricsCenter 量化指标
报警/信号/业务混合 → AlarmBus 三分区隔离
```
