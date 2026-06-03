# V10: Virtual Plant — 虚拟工厂

> 当现场没有 PLC、没有设备、不允许停机时，用 Virtual Plant 模拟整个工厂。
>
> **核心原则：只模拟设备和 PLC 反馈层，WCS Core 全部真实运行。**

---

## 为何需要 Virtual Plant

工业 WCS 项目开发中后期一定会遇到：

```
客户现场             开发团队
┌──────────┐        ┌──────────┐
│ PLC ⚠️   │        │ WCS V9   │
│ 还没到货  │        │ 需要测试  │
│ 设备未装  │        │ 需要验证  │
│ 不给停机  │        │ 不能停   │
└──────────┘        └──────────┘
         ── 矛盾 ──>
```

传统方案：写死代码模拟 → 现场联调时全推翻。

Virtual Plant 方案：

```
┌─────────────────────────────────────────────────────┐
│                  Virtual Plant                        │
│                                                       │
│  ConveyorSim  ←  TransportGenerator  ←  WMS Mock     │
│  LiftSim                                             │
│  ASRSSim      →  SimulatorSignalSource                │
│  RobotSim                                            │
│  ChaosMonkey                                          │
└─────────────────┬───────────────────────────────────┘
                  │ ISignalSource (模拟 PLC)
                  ▼
┌─────────────────────────────────────────────────────┐
│              WCS Core（全部真实运行）                    │
│                                                       │
│  SignalMapper → SignalBus → RuleEngine → TaskEngine   │
│  → StateCenter → CommandCenter → DeviceManager         │
│  → TraceCenter → ExecutionHistoryCenter                 │
└─────────────────────────────────────────────────────┘
```

**切换真实 PLC 时只需改一行配置：**

```json
// appsettings.json — 开发/测试时（无需真实 PLC/设备）
{
  "Simulator": {
    "Enabled": true,         // ← 改为 false 即切换到真实 PLC
    "TransportTps": 2,
    "FaultProbability": 0.05
  }
}

// appsettings.json — 现场部署时（连接真实 PLC）
{
  "Simulator": {
    "Enabled": false         // ← 仅改这一行
  }
}
```

Host 项目中的 Program.cs 根据此配置自动注册：

```csharp
// Program.cs 内部（已实现，不需要手动修改）
var simulatorEnabled = builder.Configuration
    .GetSection("Simulator").GetValue<bool>("Enabled");

if (simulatorEnabled)
{
    // 注册虚拟工厂（SimulatorSignalSource + VirtualPlant）
    builder.Services.AddSingleton<SimulatorSignalSource>();
    builder.Services.AddSingleton<VirtualPlant>();
    // PlcPollingBackgroundService 自动跳过
}
else
{
    // 注册真实 PLC（S7ConnectionFactory + PlcPollingService）
    builder.Services.AddSingleton<IS7ConnectionFactory>(...);
    builder.Services.AddSingleton<IPlcPollingService>(...);
    // PlcPollingBackgroundService 自动启动轮询
}
```

---

## 模块结构

```
Wcs.Simulator/
│
├── PlcSimulator/
│   ├── ISignalSource.cs             # 信号源接口（抽象真实PLC和模拟PLC）
│   └── SimulatorSignalSource.cs     # 模拟 PLC 信号源
│
├── DeviceSimulator/
│   ├── DeviceSimulatorBase.cs       # 设备模拟器基类
│   ├── ConveyorSimulator.cs         # 输送线模拟器（3s 运输延时）
│   ├── LiftSimulator.cs            # 提升机模拟器（5s 运输延时）
│   ├── AsrsSimulator.cs            # 堆垛机模拟器（8s 运输延时）
│   └── RobotSimulator.cs           # 机器人模拟器（2s 运输延时）
│
├── TransportGenerator.cs           # 运输任务生成器（TPS 可配置）
├── SignalReplayPlayer.cs          # PLC 信号日志回放器
├── ChaosMonkey.cs                 # 故障注入工具
├── ScenarioRunner.cs              # 场景运行器 + 预定义场景模板
└── VirtualPlant.cs                 # 虚拟工厂门面（一键启动）
```

---

## 设备模拟器

### 模拟什么

**只模拟设备行为和 PLC 反馈信号，不模拟 Core 逻辑。**

```
Task → CommandCenter → DeviceSimulator
                            │
                    模拟运输耗时
                            │
                    ↓ 3 秒后
            Emit("CV01.Arrived", true)
                            │
                    进入 SignalMapper → SignalBus
                            ↓
                      Core 全部真实运行
```

### 输送线模拟器

```csharp
var cv = plant.AddConveyor("CV01");

// 内部流程：
// 1. IsBusy = true
// 2. await Task.Delay(3000)  ← 模拟输送时间
// 3. Emit("CV01.Arrived", true)
// 4. Emit("CV01.Arrived", false)  ← 清除信号
// 5. IsBusy = false
```

### 提升机模拟器

```csharp
var lift = plant.AddLift("LIFT01", transportMs: 5000);

// 模拟指定楼层
await lift.MoveToFloor(3);
// → Emit("LIFT01.FloorReached", true, {"floor":3})
```

### 堆垛机模拟器

```csharp
var asrs = plant.AddAsrs("ASRS01", transportMs: 8000);

// 模拟取货
await asrs.RetrieveAsync();
// → Emit("ASRS01.RetrieveCompleted", true)
```

---

## TransportGenerator（运输生成器）

每秒生成随机运输任务，模拟 WMS 下发任务。

```csharp
// 每秒 2 个任务，随机 Source→Target
generator.TasksPerSecond = 2;
await generator.StartAsync(ct);

// 自动生成的任务：
// Task1: RECV_DOCK_A → ASRS_01  (Pallet=PALLET_0001)
// Task2: RECV_DOCK_B → ASRS_03  (Pallet=PALLET_0002)
```

---

## SignalReplayPlayer（信号回放器）

重放真实 PLC 录制的信号日志，100% 还原现场工况。

日志格式（JSON）：
```json
[
  { "time": "09:01:00", "signal": "CV01.Arrived",  "value": true  },
  { "time": "09:01:10", "signal": "Lift01.Ready",   "value": true  },
  { "time": "09:01:20", "signal": "CV01.Arrived",   "value": false }
]
```

```csharp
var result = await replayPlayer.PlayFileAsync("day1.json");
// → TotalSignals: 5000, EmittedSignals: 5000, Duration: 00:30:00
```

---

## ChaosMonkey（混沌猴子）

随机注入故障，验证 WCS Core 的恢复能力。

| 故障类型 | 概率 | 恢复行为 |
|---------|------|---------|
| 设备故障 | 5% | 5~30 秒后自动恢复 |
| PLC 断线 | 5% | 3~15 秒后自动重连 |
| 信号风暴 | 5% | 100 个噪声信号冲击 |

```csharp
chaos.FaultProbability = 0.10; // 10% 概率
await chaos.StartAsync(ct);    // 随机间隔注入
```

---

## 预定义测试场景

| 场景 | TPS | 时长 | 故障率 | 用途 |
|------|-----|------|--------|------|
| `QuickTest` | 1 | 2 min | 0% | 快速验证 |
| `StressTest` | 10 | 30 min | 0% | 压力测试 |
| **`ResilienceTest`** | **3** | **60 min** | **10%** | **韧性测试** |
| `LongRunTest` | 5 | 72 h | 5% | 长期稳定性 |

```csharp
// 一键运行韧性测试
await plant.RunScenarioAsync(ScenarioTemplate.ResilienceTest());
```

---

## VirtualPlant 门面

```csharp
// Program.cs — 一行切换
services.AddSingleton<VirtualPlant>();

// 测试代码
var plant = sp.GetRequiredService<VirtualPlant>();
plant.BuildDefaultTopology();  // 7 个设备
await plant.QuickTestAsync();  // 2 分钟快速测试
```

---

## 验证

- **Wcs.Simulator**：`dotnet build` — 0 errors
- **Wcs.Core.Tests**：`dotnet test` — **108/108 全部通过**

---

## 一句话总结 V10

```
Virtual Plant = SimulatorSignalSource + DeviceSimulators(4种) +
                TransportGenerator + SignalReplayPlayer + ChaosMonkey
              = 无需任何真实 PLC/设备即可运行完整 WCS Core 测试
              = 切换真实 PLC 只需改一行 DI 注册
```
