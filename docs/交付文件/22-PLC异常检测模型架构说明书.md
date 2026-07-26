# PLC 异常检测模型架构说明书

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 系统 | WCS Runtime Engine |
| 能力 | PLC 时序数据、规则异常检测、统计基线、Isolation Forest 训练模型 |
| 文档版本 | V1.0 |
| 功能实现基线 | `develop@1e7cda6b8d6dfd230293575edc5680bb383971e6` |
| 文档状态 | 研发交付版 |
| 适用对象 | 架构、研发、测试、实施、运维、设备与工艺人员 |

## 2. 目标和边界

本架构用于在不阻塞 PLC 轮询和 WCS 实时调度的前提下，对 PLC 信号进行可靠采集、历史存储、规则检测、统计检测和训练型异常识别，并将确认后的异常统一纳入 SQL 生命周期、AlarmCenter 和运维查询。

本架构不负责：

- 代替 PLC 本身的安全联锁、急停和保护逻辑；
- 直接控制设备动作；
- 自动把所有模型异常升级为停机指令；
- 在没有现场正常数据和验收门槛时自动启用机器学习；
- 通过互联网调用外部模型服务。

## 3. 核心设计原则

1. **实时状态与历史数据分离**：StateCenter 是当前状态真相；时序数据库用于历史、趋势和训练。
2. **采集与存储解耦**：PLC 轮询线程不同步等待 SQL Server 或 InfluxDB。
3. **确定性规则优先**：安全、互斥、顺序和状态一致性优先使用可解释规则。
4. **机器学习作为补充**：Isolation Forest 发现未知组合偏移，不替代确定性规则。
5. **候选异常不等于正式异常**：必须经过连续计数、恢复计数和生命周期状态机。
6. **默认关闭**：规则引擎和机器学习均需显式配置后启用。
7. **模型可追溯**：每条机器学习异常记录模型版本、分数、阈值和解释。
8. **离线可运行**：模型训练和推理均为纯 .NET，不依赖 Python、GPU 或外网。

## 4. 总体数据流

```text
Siemens PLC / Simulator
        ↓
S7PollingService / SimulatedPlcPollingService
        ↓
PlcBlockDiffEngine + EventDetector
        ↓
RawSignalEvent（可携带 SourceTimestampUtc）
        ↓ EventBus
        ├──────────────────────────────────────────────┐
        │                                              │
        ↓                                              ↓
PlcTelemetryEventBridgeService              PlcAnomalySampleFactory
        ↓                                              ↓
Telemetry Buffer / WAL                    PlcAnomalySample
        ↓                                              ↓
SQL Server / InfluxDB          ┌───────────┴────────────┐
                               ↓                        ↓
                    AnomalyEngine v1         AnomalyEngine v2
                    规则 + MAD + 一致性       特征窗口 + Isolation Forest
                               └───────────┬────────────┘
                                           ↓
                               连续异常/恢复状态机
                                           ↓
                              PlcAnomalyRaisedEvent
                              PlcAnomalyRecoveredEvent
                                           ↓
                         SQL 生命周期 + AlarmCenter + API
```

## 5. 分层结构

### 5.1 数据采集层

主要组件：

- `S7PollingService`；
- `SimulatedPlcPollingService`；
- `PlcBlockDiffEngine`；
- `EventDetector`；
- `RawSignalEvent`。

`RawSignalEvent` 包含 PLC、DB 块、字段名、新旧值、边沿、校验结果和领域事件类型。`SourceTimestampUtc` 可用于历史回放和测试；未提供时使用事件发生时间。

### 5.2 时序存储层

核心抽象：

- `IPlcTelemetryStore`；
- `PlcTelemetryBuffer`；
- `PlcTelemetryBatchWriterService`；
- `FilePlcTelemetrySpool`。

支持 Provider：

- `Disabled`；
- `SqlServer`；
- `InfluxDb`。

支持耐久模式：

- `Buffered`：有界内存队列，数据库故障时转入本地 spool；
- `WriteAhead`：事件返回“已接收”前先完成 WAL 刷盘。

时序数据不是任务恢复、锁恢复和报警生命周期的唯一真相；这些业务状态仍保存在 SQL Server 或运行时恢复体系中。

### 5.3 异常样本转换层

`PlcAnomalySampleFactory` 负责：

- 统一 UTC 时间；
- 从字段名提取 DeviceId；
- 解析布尔值和数值；
- 保留 EventId、PLC、DB 块、新旧值和来源；
- 屏蔽 SQL Server、InfluxDB 等存储差异。

### 5.4 规则与统计引擎 v1

`PlcAnomalyEngine` 支持：

- 上下限检测；
- 最大变化率；
- Boolean true 持续时间；
- Median/MAD 动态统计基线；
- 跨信号一致性；
- 连续异常确认；
- 连续正常恢复；
- 状态和窗口 TTL 淘汰。

跨信号例子：

```text
Running=true 且 Speed=0
Busy=false 但 Task=Running
两个互斥区段同时占用
```

v1 仅在规则启用统计基线时创建数值环形缓冲；仅缓存一致性规则真正需要的关联信号。

### 5.5 特征窗口层 v2

`PlcFeatureWindowEngine` 按 Profile 和设备创建固定时间窗口。

数值信号特征：

- mean；
- standard deviation；
- min；
- max；
- last；
- slope；
- range；
- samples per second。

布尔信号特征：

- true ratio；
- transitions；
- last；
- samples per second。

窗口层使用在线统计量，不长期保存窗口内所有原始采样点。

### 5.6 Isolation Forest 模型层

主要组件：

- `IsolationForestTrainer`；
- `IsolationForestPredictor`；
- `PlcMlAnomalyEngine`；
- `FilePlcMlTrainingStore`；
- `FilePlcMlModelStore`。

训练流程：

```text
明确标识为正常的数据窗口
→ 固定特征顺序
→ 稳定排序
→ 80% 建森林
→ 20% 独立校准阈值
→ 生成版本化模型
→ 原子激活 active.json
```

Isolation Forest 使用确定性随机种子，同一训练集、同一配置应产生一致的模型结构和阈值。

### 5.7 决策和生命周期层

机器学习采用两级阈值：

```text
观察阈值 = max(模型校准阈值, Profile.ObserveThreshold)
正式阈值 = max(观察阈值, Profile.WarningThreshold)
```

低于正式阈值的偏离只计入观察指标，不推进正式异常连续次数。

正式异常必须满足：

- 分数达到正式阈值；
- 连续异常窗口达到 `ConsecutiveAbnormalCount`；
- 当前不存在相同 AnomalyKey 的活动生命周期。

恢复必须满足连续正常窗口达到 `ConsecutiveRecoveryCount`。

### 5.8 持久化和报警层

确认后的生命周期通过事件总线进入：

- `PlcAnomalyPersistenceService`：写入 `Wcs_PlcAnomaly`；
- `PlcAnomalyAlarmBridgeService`：按 `RaiseAlarm` 接入 AlarmCenter；
- `PlcAnomalyController` / `PlcMlAnomalyController`：状态、活动异常和模型管理查询。

SQL 只保存正式激活和恢复后的生命周期，不保存每一个模型分数，避免形成新的高频业务表。

## 6. 核心数据模型

### 6.1 PlcAnomalyRecord

关键字段：

- `AnomalyId`：生命周期唯一编号；
- `AnomalyKey`：规则、设备、信号或 Profile 的幂等键；
- `RuleId`；
- `Type`：Threshold、RateOfChange、Duration、StatisticalBaseline、Consistency、MachineLearning；
- `Severity`；
- `Status`：Active / Recovered；
- `DetectorName`；
- `ModelVersion`；
- `Score`、期望值和上下界；
- `StartTimeUtc`、`LastSeenUtc`、`EndTimeUtc`；
- `Reason`、`ContextJson`、`TaskId`。

### 6.2 PlcIsolationForestModel

模型文件包含：

- ProfileId；
- Version；
- CreatedUtc；
- FeatureNames；
- 标准化均值和标准差；
- Isolation Forest 树；
- 训练样本数；
- 校准样本数；
- 子采样大小；
- 决策阈值；
- 污染率。

## 7. 模型版本和回滚

目录示例：

```text
data/anomaly-models/
└── ML-CV-CURRENT/
    ├── model-20260725142342-883e7b2903.json
    ├── model-20260725142343-67a55ca399.json
    └── active.json
```

安全规则：

1. 候选模型必须先完成反序列化和特征顺序校验；
2. 在线推理与模型切换使用同一 Profile 异步锁；
3. 有活动机器学习异常时拒绝切换模型；
4. `active.json` 通过临时文件、强制刷盘和原子重命名更新；
5. Host 重启后从 `active.json` 加载活动版本；
6. 历史生命周期保留原模型版本，不因回滚被改写。

## 8. 并发、内存和容量治理

### 8.1 v1 状态治理

- `MaximumTrackedRuleSignals` 限制追踪状态；
- `InactiveStateRetentionSeconds` 淘汰已恢复且空闲状态；
- `RelatedSampleRetentionSeconds` 清理关联快照；
- `MaximumCleanupItemsPerSweep` 限制单次清理工作量；
- 活动异常和持续 true 计时器不会被淘汰。

### 8.2 v2 状态治理

- `MaximumTrackedWindows` 限制窗口数量；
- `InactiveInferenceStateRetentionSeconds` 清理长期不活跃推理状态；
- Profile 锁隔离不同设备族的训练、推理和切换；
- 训练数据文件受 `MaximumTrainingWindows` 限制。

### 8.3 时序写入治理

- 有界 Channel；
- 批量写入；
- 数据库故障 spool；
- 可选 WAL；
- Provider 恢复后顺序重放；
- SQL EventId 和 Influx 时间戳主键保证幂等。

## 9. 可用性和故障语义

| 场景 | 行为 |
|---|---|
| SQL/Influx 短暂不可用 | 数据进入 spool 或 WAL，恢复后重放 |
| Host 正常停止 | 队列剩余数据写入本地持久化缓冲 |
| Host `kill -9` | WriteAhead 模式下已确认接收的数据可恢复 |
| 模型文件损坏 | 模型加载失败并记录错误，不静默启用损坏模型 |
| 无活动模型 | Profile 可继续收集明确允许的训练窗口，但不执行正式推理 |
| 模型管理 API 关闭 | 训练、列表、激活接口返回 404 |
| 活动异常期间切换模型 | 返回冲突，拒绝切换 |

整机突然断电的最终保证仍取决于文件系统、磁盘控制器缓存和存储硬件断电保护。

## 10. 安全设计

- `MachineLearning.Enabled` 默认 false；
- `ManagementApiEnabled` 默认 false；
- LoadTest 控制器只在特定环境暴露；
- 模型与训练目录必须使用受控 ACL；
- 禁止将生产数据库密码、Influx Token 和模型敏感数据提交仓库；
- 模型训练应由授权人员发起并记录数据范围、配置、版本和审批；
- 机器学习异常默认不得直接写 PLC 或触发运动命令。

## 11. 部署拓扑

推荐单站点部署：

```text
PLC 网络
  ↓
Wcs.Host
  ├── StateCenter / EventBus / AnomalyEngine
  ├── 本地 WAL / 模型 / 训练数据目录
  ├── SQL Server（业务、异常生命周期、可选 telemetry）
  └── InfluxDB（可选 PLC 时序历史）
```

模型目录应与应用发布目录分离，升级 Host 时不得覆盖 `data/anomaly-models` 和 `data/anomaly-training`。

## 12. 已知限制和演进方向

当前版本只支持单变量 Profile 内的固定特征向量和 Isolation Forest。后续可扩展：

- 多信号联合 Profile；
- 按产品、负载、模式拆分模型；
- 训练数据审批和标签工作流；
- 模型漂移监控；
- 自动影子评估和候选模型对比；
- ONNX 模型适配器；
- 异常根因图和设备传播分析。

在这些能力完成前，不应使用单一模型覆盖不同设备类型、不同工况和不同产品上下文。