# PLC 异常检测功能说明书

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 文档版本 | V1.0 |
| 功能实现基线 | `develop@1e7cda6b8d6dfd230293575edc5680bb383971e6` |
| 功能范围 | PLC 时序存储、规则异常、统计基线、Isolation Forest、生命周期、模型管理 |
| 默认状态 | Telemetry 使用 SQL Server；规则引擎和机器学习默认关闭 |

## 2. 功能概览

PLC 异常检测能力由三部分组成：

1. **可靠数据层**：选择 SQL Server 或 InfluxDB 保存 PLC 时序数据，并通过 Buffered 或 WriteAhead 模式控制耐久性。
2. **异常检测 v1**：使用阈值、变化率、持续时间、Median/MAD 和跨信号一致性规则识别可解释异常。
3. **异常检测 v2**：从正常窗口训练 Isolation Forest，识别未知的多特征偏移，并复用 v1 生命周期和报警体系。

## 3. 功能矩阵

| 能力 | v1 规则/统计 | v2 Isolation Forest | 是否直接报警 |
|---|---:|---:|---:|
| 固定上下限 | 支持 | 间接学习 | 可配置 |
| 最大变化率 | 支持 | 可体现在 slope/range | 可配置 |
| Boolean true 超时 | 支持 | 不作为首选 | 可配置 |
| Median/MAD 动态基线 | 支持 | 不依赖 | 可配置 |
| 跨信号一致性 | 支持 | 当前不支持跨信号联合窗口 | 可配置 |
| 未知组合偏移 | 有限 | 支持 | 连续确认后可配置 |
| 异常分数 | 支持 | 支持 | 不以单次分数直接报警 |
| 模型版本 | 不适用 | 支持 | 记录到生命周期 |
| 模型回滚 | 不适用 | 支持 | 活动异常时禁止 |
| SQL 生命周期 | 支持 | 支持 | 统一实现 |
| AlarmCenter | 支持 | 支持 | 由 RaiseAlarm 控制 |

## 4. PLC 时序存储功能

### 4.1 Provider 选择

`Storage:Telemetry:Provider` 支持：

- `Disabled`：不保存 PLC 时序历史；
- `SqlServer`：写入 `Wcs_PlcTelemetry`；
- `InfluxDb`：写入 InfluxDB。

任务、资源锁、报警、调度配置和异常生命周期等业务数据不随 Telemetry Provider 切换，仍使用 SQL Server。

### 4.2 耐久模式

| 模式 | 接收语义 | 适用场景 |
|---|---|---|
| Buffered | 先进入有界内存队列；数据库故障时批量转 spool | 常规高吞吐现场 |
| WriteAhead | WAL 刷盘完成后才确认接收 | 强耐久、允许增加磁盘 IO 的现场 |

### 4.3 状态查询

`GET /api/telemetry/status` 用于查看：

- Provider；
- durabilityMode；
- accepted / persisted / dropped；
- queue / in-flight；
- walPending / spoolPending；
- conservationDelta；
- 最近错误。

## 5. 规则异常功能 v1

### 5.1 阈值检测

规则可配置 `Minimum` 和 `Maximum`。

典型用途：

- 温度超上限；
- 电流超上限；
- 速度反馈低于最低值；
- 压力低于允许范围。

### 5.2 变化率检测

`MaximumRatePerSecond` 用于限制相邻样本的单位秒变化量。

典型用途：

- 电流突然上升；
- 位置或速度反馈跳变；
- 温度在短时间内异常变化。

变化率检测必须结合采样时间，历史回放时应提供 PLC 原始 UTC 时间。

### 5.3 持续时间检测

`MaximumTrueDurationMs` 适用于布尔信号。

典型用途：

- Busy 长时间不释放；
- 到位信号长期保持；
- 请求位或占用位超时；
- 故障位持续存在。

持续时间检测由周期 Sweep 推进，不要求信号持续产生变化沿。

### 5.4 Median/MAD 动态基线

启用 `StatisticalBaselineEnabled` 后，规则使用固定容量窗口计算中位数和 MAD。

优势：

- 对少量离群点不敏感；
- 比均值/标准差更适合工业噪声；
- 不需要训练模型文件；
- 可用于电流、周期、速度等单信号偏移。

规则可通过 `MadMultiplier` 和 `MinimumMad` 控制敏感度。

### 5.5 跨信号一致性

配置 `RelatedSignalPattern` 后，规则进入一致性模式。

支持：

- 主信号等于指定值时，关联信号必须等于指定值；
- 关联数值必须位于最小值和最大值之间；
- 关联样本必须在 `MaximumRelatedAgeMs` 内。

典型例子：

```text
*_Running = true 时，*_Speed 必须 > 0
```

### 5.6 连续确认与恢复

每条规则可单独配置：

- `ConsecutiveAbnormalCount`；
- `ConsecutiveRecoveryCount`。

未配置时使用全局参数。单次毛刺只增加观察，不直接建立正式生命周期。

## 6. 训练型异常功能 v2

### 6.1 Profile

一个 `PlcMlProfile` 描述一类同构设备：

- PLC 匹配模式；
- DeviceId 匹配模式；
- 窗口长度；
- 每信号最低样本数；
- 信号列表和类型；
- 训练数量；
- 森林参数；
- 阈值和连续计数；
- 是否接入 AlarmCenter。

不同设备类型、不同工况和不同产品应使用不同 Profile 或模型版本。

### 6.2 训练数据采集

只有 `CollectTrainingData=true` 的 Profile 才保存窗口特征。

训练数据必须满足：

- 由人工或业务流程确认属于正常运行；
- 不包含故障恢复、手动调试、急停和未知状态；
- 覆盖启动、稳定运行、不同负载和允许波动；
- 记录采集时间、现场版本和设备范围。

### 6.3 手动和自动训练

- `AutoTrain=false`：由管理 API 手动训练；
- `AutoTrain=true`：达到最低训练窗口后可自动训练。

生产建议首次使用手动训练和审批，待数据治理成熟后再考虑自动训练。

### 6.4 训练校准

训练窗口按稳定顺序处理：

- 80% 用于建树；
- 20% 只用于阈值校准；
- `Contamination` 表示校准集中允许的尾部比例；
- 最终观察阈值不能低于 Profile 安全下限。

### 6.5 在线推理

推理结果包含：

- ProfileId；
- ModelVersion；
- Score；
- DecisionThreshold；
- IsAnomaly；
- Explanation。

低分偏离可以被观察，但只有达到正式阈值并满足连续次数才激活异常。

### 6.6 模型解释

模型解释基于偏离最明显的特征，帮助定位：

- 均值偏移；
- 波动增大；
- 斜率异常；
- 最大/最小范围异常；
- 采样密度异常；
- Boolean true 比例或切换次数异常。

该解释用于辅助诊断，不等同于经过验证的根因结论。

## 7. 异常生命周期

### 7.1 生命周期状态

```text
Normal
  ↓ 连续异常达到门槛
Active
  ↓ 连续正常达到恢复门槛
Recovered
```

### 7.2 SQL 持久化

正式生命周期写入 `Wcs_PlcAnomaly`。

主要信息：

- 设备、PLC、DB 块和信号；
- 规则或 Profile；
- 异常类型和严重级别；
- 分数、期望范围；
- 开始、最后出现和恢复时间；
- DetectorName、ModelVersion；
- Reason 和 ContextJson；
- 是否需要接入 AlarmCenter。

同一 AnomalyKey 在活动期间不会重复插入多行。

### 7.3 AlarmCenter 桥接

`RaiseAlarm=true` 时，正式异常进入 AlarmCenter；恢复后发布恢复事件。

`Observe` 严重级别只记录观察，不应形成现场报警。

## 8. API 功能

### 8.1 v1 状态

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/anomaly/status` | 查询规则引擎状态、缓存和淘汰指标 |
| GET | `/api/anomaly/active` | 查询当前活动异常 |

`POST /api/anomaly/load` 仅在 `LoadTest` 环境存在，不属于生产 API。

### 8.2 v2 状态和管理

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/anomaly/ml/status` | 查询 Profile、活动模型和推理指标 |
| POST | `/api/anomaly/ml/train/{profileId}` | 训练并激活新模型 |
| GET | `/api/anomaly/ml/models/{profileId}` | 查询模型版本 |
| POST | `/api/anomaly/ml/models/{profileId}/{version}/activate` | 激活或回滚版本 |

后三个管理接口只有 `ManagementApiEnabled=true` 时存在。

错误语义：

- 404：管理 API 关闭、Profile 或模型不存在；
- 400：参数、特征或模型内容不合法；
- 409：存在活动异常或当前状态不允许训练/切换。

## 9. 状态与监控指标

### 9.1 v1

- ProcessedSamples；
- MatchedRuleEvaluations；
- DetectorObservations；
- Raised / Recovered / ActiveAnomalies；
- Suppressed / Failures；
- TrackedRuleSignals；
- StatisticalWindows；
- TrackedDeviceSnapshots；
- TrackedRelatedSamples；
- EvictedRuleStates；
- EvictedRelatedSamples；
- EvictedDeviceSnapshots；
- LastProcessedUtc / LastError。

### 9.2 v2

- ActiveModelVersion；
- TrainingWindowCount；
- CompletedWindows；
- DroppedIncompleteWindows；
- Predictions；
- AnomalyObservations；
- Raised / Recovered / ActiveAnomalies；
- TrackedWindows；
- TrackedInferenceStates；
- Failures / LastError。

## 10. 故障处理功能

| 故障 | 系统行为 |
|---|---|
| 时序数据库不可用 | spool/WAL 缓冲，恢复后重放 |
| 模型目录不可写 | 训练失败并记录错误，不替换活动模型 |
| 活动模型不存在 | 不进行机器学习推理 |
| 特征顺序不一致 | 拒绝加载或切换模型 |
| 模型文件损坏 | 加载失败并记录日志 |
| Profile 达到窗口上限 | 通过容量和过期策略治理 |
| 关联信号过期 | 一致性检测不使用陈旧数据 |
| SQL 生命周期写入暂时失败 | 持久化服务重试并记录日志 |

## 11. 功能限制

- 当前 ML Profile 主要针对单类信号窗口，不等同于完整多变量时序神经网络；
- 模型不会自动判断数据是否真的“正常”，训练数据选择仍需业务确认；
- 模型分数不能代替设备安全规则和 PLC 联锁；
- 不同现场的阈值和模型不可直接复制后投产；
- 现场使用前必须完成影子运行、误报复核和设备工程师签署。