# WCS 架构修正 V10 — 5 个偏差分析与 EventDetector

> 基于实际工业现场经验，对 V9 架构中 5 个关键偏差的修正方案。

---

## 5 个偏差总览

| # | 偏差 | 问题 | 修正 |
|---|------|------|------|
| 1 | Running 触发任务 | 设备状态 ≠ 业务事件，设备一启动就疯狂生成任务 | 新增 **EventDetector** 层，只监测边沿信号→业务事件 |
| 2 | Validator 阻断 StateCenter | 验证器拒绝后 StateCenter 不更新，UI 看到的是假状态 | **StateCenter 永远同步 PLC**，验证器只拦截事件 |
| 3 | WaitNode 轮询 StateCenter | while 循环等状态 = CPU 空转 | 直接用 **EventBus 事件驱动**，0 CPU 等待 |
| 4 | CommandCenter 五态死板 | 西门子现场很多没有 Ack/Done 信号 | 改为 **CommandProfile 可配置状态机** |
| 5 | Validator 和任务关联混乱 | 信号验证器不应关心任务 ID | 明确分层：**信号验证器 vs DAG 验证器** |

---

## 修正后的完整架构

```
PLC
 │
 ▼
S7PollingService (Timer 轮询)
 │ byte[] → Struct.FromBytes<T> → 强类型 struct
 │
 ├─ 第一步：StateCenter 同步 ────────────── (永远同步，不经过验证器)
 │     └─ DeviceState["CV01"].Running = true
 │     └─ DeviceState["CV01"].Fault = false
 │
 └─ 第二步：EventDetector ───────────────── (边沿检测 → 业务事件)
       │
       ├─ 检测字段变化
       ├─ false→true 上升沿
       ├─ 命名约定推断 或 精确规则匹配
       │
       ├─ CV01_RequestOut false→true → PalletArrivedEvent
       ├─ LIFT01_Fault false→true    → DeviceFaultEvent
       └─ ASRS01_Ready false→true    → ConveyorReadyEvent
            │
            ├─ 验证管道 (ISignalValidator)
            │   ├─ Pass   → 发布到 EventBus
            │   └─ Reject → 丢弃事件（StateCenter 已更新不受影响）
            │
            └─ EventBus → RuleEngine → TaskScheduler → ChainExecutionEngine
                                                         │
                                           DecisionNode(任务级验证)
                                           ActionNode → CommandCenter → WritePool → PLC
                                           WaitNode(事件驱动，0 CPU)
```

---

## EventDetector 详细设计

### 定位

**EventDetector 是 PLC 世界和 Task 世界的桥梁。**

```
之前：
  PLC 字段变化 → StateCenter 更新 → ... 然后呢？信号怎么变成任务？

现在：
  PLC 字段变化 → StateCenter 更新 + EventDetector 边沿检测
                   → CV01_RequestOut 上升沿 → PalletArrivedEvent
                   → RuleEngine 收到事件 → 生成 Task
```

### 边沿检测

| 旧值 | 新值 | 边沿 | 是否产生事件 |
|------|------|------|------------|
| false | true | **上升沿** | ✅ 是（通常） |
| true | false | 下降沿 | ❌ 否（通常） |
| false | false | 无变化 | ❌ |
| true | true | 无变化 | ❌ |

### 命名约定推断

按字段名后缀自动推断事件类型，**无需逐条配置**：

| 字段名后缀 | 推断事件 | 示例 |
|-----------|---------|------|
| `_Arrived` / `_RequestOut` | `PalletArrivedEvent` | `CV01_PalletArrived` |
| `_Fault` | `DeviceFaultEvent` | `LIFT01_Fault` |
| `_Ready` | `ConveyorReadyChangedEvent` | `ASRS01_Ready` |
| `_Speed` / `_Count` | 值变化事件 | `CV01_Speed` |

### 精确规则配置

需要精确控制时可以逐条配置：

```json
{
  "EventDetectionRules": [
    {
      "RuleId": "CV01.PalletArrived",
      "DeviceId": "CV01",
      "FieldName": "CV01_PalletArrived",
      "Edge": "Rising",
      "TargetEventType": "Wcs.Core.EventBus.Events.PalletArrivedEvent"
    }
  ]
}
```

---

## 三个关键原则

### 原则 1：StateCenter 永远同步 PLC

```
✅ 正确：
  PLC → StateCenter 更新（无条件）→ EventDetector → 验证器 → EventBus
  StateCenter 始终反映真实 PLC 状态，验证器拒绝不影响 StateCenter

❌ 错误（V9）：
  PLC → 验证器 → 拒绝 → StateCenter 不更新 → UI 看到假状态
```

**为什么必须这样：** 报警系统、监控面板、操作员界面必须看到真实 PLC 状态。验证器拦截的只是"业务事件"，不应该屏蔽 PLC 的真实状态。

### 原则 2：WaitNode 用事件驱动，不用轮询

```
✅ 正确（V8 Subscribe-Then-Check）：
  await eventBus.WaitAsync<PalletArrivedEvent>(
      e => e.DeviceId == "CV01", timeout);
  // 0 CPU，无竞态

❌ 错误：
  while (StateCenter.GetDeviceState("CV01").Status != Running)
      await Task.Delay(100);
  // CPU 空转，延迟取决于轮询间隔
```

### 原则 3：验证器分层

| 层级 | 名称 | 运行位置 | 职责 | 输入 |
|------|------|---------|------|------|
| 设备级 | PLC 信号验证器 | `EventDetector` | 验证 PLC 信号合法性 | `ValidatorContext`（struct + StateCenter + DB） |
| 任务级 | DAG 验证器 | `DecisionNode` | 验证任务执行条件 | StateCenter + 任务上下文 |

---

## CommandProfile — 可配置命令状态机

不同设备有不同反馈能力，不再强制五态：

```csharp
// 输送线（只有 Start/Busy）
new CommandProfile { HasAck = false, HasBusy = true, HasDone = false }

// 堆垛机（完整五态）
new CommandProfile { HasAck = true, HasBusy = true, HasDone = true }

// 简单 IO（无反馈）
new CommandProfile { HasAck = false, HasBusy = false, HasDone = false }
```

状态机根据 Profile 动态生成：

```
输送线:       Sent → Executing → Completed
堆垛机:       Sent → Acked → Executing → Done → Completed
简单 IO:      Sent → Completed
```

---

## 新增/修改文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `EventDetection/EventDetector.cs` | 新建 | 边沿检测 + 命名约定推断 + 业务事件生成 |
| `EventDetection/EventDetectionRule.cs` | 新建 | 检测规则模型 |
| `CommandCenter/CommandProfile.cs` | 新建 | 可配置命令状态机 |
| `PlcSubsystem/S7/S7PollingService.cs` | 重写 | StateCenter 先更新 + 集成 EventDetector |
| `Application/PlcRegistrationExtension.cs` | 更新 | 注册 EventDetector |
| `docs/architecture-corrections-v10.md` | 新建 | 本文 |

## Build & Test

- `dotnet build` — 0 errors ✅
- `dotnet test` — 108/108 ✅
