# PLC 异常检测运行上下文与同群对比说明书

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能名称 | AnomalyEngine v3.1 — 运行上下文与同类设备横向对比 |
| 适用分支 | `develop` |
| 检测器 | `ContextualPeerMedianMad` |
| 异常类型 | `ContextualPeerComparison` |
| 模型版本标识 | `peer-mad-v1` |
| 默认状态 | 关闭，需显式设置 `PeerComparisonEnabled=true` |

## 2. 建设目标

Isolation Forest 解决的是“当前设备窗口是否偏离历史正常分布”，但工业现场还存在另一类异常：

- 同一批同型号电机中，只有一台电流明显偏高；
- 同一路线、同负载、同产品条件下，只有一台 RGV 周期明显变慢；
- 单个信号仍在允许范围内，但与同工况设备相比已经明显劣化；
- 空载、满载、手动、自动等不同工况不能使用同一基线直接比较。

v3.1 在现有规则、统计和 Isolation Forest 模型之外增加可解释的同群横向检测能力：

```text
RawSignalEvent
→ PlcAnomalySampleFactory
→ OperatingContextCenter
→ 独立特征窗口
→ Profile + Context + Window 分组
→ Median / MAD 横向比较
→ 连续异常与恢复状态机
→ Shadow / Canary / Active 分流
→ Wcs_PlcMlCandidate
→ Wcs_PlcAnomaly / AlarmCenter
```

该检测器不训练深度学习或黑盒模型，也不会修改现有 Isolation Forest 的特征名称、顺序、训练文件和活动模型。

## 3. 核心设计

### 3.1 运行上下文

`ContextSignals` 用于描述比较条件，例如：

- `Mode=Auto`；
- `Load=Full`；
- `Product=FDY-A`；
- `Route=R01`；
- `TaskType=Inbound`。

上下文信号只参与分组，不进入 Isolation Forest 特征向量。

上下文键按配置顺序生成：

```text
Mode=Auto|Load=Full|Product=FDY-A
```

上下文样本超过 `MaximumAgeSeconds` 后失效，并使用 `DefaultValue`。这样可以避免陈旧工况继续参与新窗口比较。

### 3.2 同群边界

只有满足以下条件的设备才会放入同一个比较桶：

```text
ProfileId 相同
+ ContextKey 相同
+ WindowStartUtc 相同
```

不同产品、不同负载、不同运行模式或不同时间窗口不会相互比较。

### 3.3 特征窗口

同群检测使用独立的 `PlcFeatureWindowEngine` 实例，不与 Isolation Forest 共享可变窗口状态。

数值信号特征包括：

- mean；
- stddev；
- min；
- max；
- last；
- slope；
- range；
- samplesPerSecond。

布尔信号特征包括：

- trueRatio；
- transitions；
- last；
- samplesPerSecond。

### 3.4 Median / MAD

每个同群桶对每个特征分别计算：

```text
中位数 Median
绝对中位差 MAD
稳健尺度 = max(MAD × 1.4826, MinimumPeerMad)
偏离度 = |设备值 - 中位数| / 稳健尺度
```

当任一特征的最大偏离度达到 `PeerMadMultiplier` 时，该设备窗口被视为异常观察。

Median/MAD 对少数离群设备不敏感，适用于“多数设备正常、少数设备偏离”的横向比较场景。

### 3.5 最小同群数量

当同一上下文、同一窗口内的设备数量小于 `MinimumPeerDevices` 时，不执行比较并记录 `SkippedBuckets`。

这可以避免两三台设备互相作为基线导致结论不稳定。

## 4. 生命周期

### 4.1 连续确认

同群偏离不会因为一个窗口直接报警，除非项目将连续数配置为 1。

```text
连续异常窗口数 >= ConsecutivePeerAbnormalCount
→ 激活候选生命周期
```

```text
连续正常窗口数 >= ConsecutivePeerRecoveryCount
→ 恢复候选生命周期
```

### 4.2 上下文切换

同一设备切换运行上下文时：

1. 旧上下文的连续异常和恢复计数清零；
2. 新上下文从第一个窗口重新计数；
3. 旧上下文存在活动异常时，在新上下文窗口开始时间立即恢复；
4. 旧工况异常不会污染或直接激活新工况异常。

例如：

```text
Auto 下偏离 1 次
→ 切换 Manual
→ Manual 从 1 重新计数，而不是继承为第 2 次
```

### 4.3 异常记录

正式异常记录主要字段：

| 字段 | 内容 |
|---|---|
| Type | `ContextualPeerComparison` |
| DetectorName | `ContextualPeerMedianMad` |
| ModelVersion | `peer-mad-v1` |
| RuleId | `PEER:<ProfileId>` |
| Score | 最大稳健偏离度 |
| ActualValue | 当前设备特征值 |
| ExpectedValue | 同群中位数 |
| LowerBound / UpperBound | Median ± PeerMadMultiplier × 稳健尺度 |
| ContextJson | 上下文、窗口、特征、中位数、MAD、偏离度、同群数量 |

## 5. 发布治理

同群检测复用 v2.1 的发布模式。

### 5.1 Shadow

- 产生 `Wcs_PlcMlCandidate`；
- 不产生 `Wcs_PlcAnomaly`；
- 不进入 AlarmCenter；
- 用于人工复核和阈值整定。

### 5.2 Canary

按 `ProfileId + DeviceId` 的稳定哈希确定是否进入正式生命周期。

- 同一设备每次路由结果一致；
- 未命中的设备仍以 Shadow 候选方式记录；
- 可使用 `CanaryPercentage` 从小比例逐步扩大。

### 5.3 Active

所有正式同群异常进入：

```text
Wcs_PlcAnomaly
→ PlcAnomalyAlarmBridgeService
→ AlarmCenter（PeerRaiseAlarm=true 时）
```

## 6. 配置示例

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": true,
      "Profiles": [
        {
          "ProfileId": "RGV-DRIVE-CURRENT",
          "Enabled": true,
          "PlcPattern": "RGV-PLC-*",
          "DevicePattern": "RGV*",
          "WindowSeconds": 10,
          "MinimumSamplesPerSignal": 20,
          "DeploymentMode": "Shadow",
          "CanaryPercentage": 0,
          "Signals": [
            {
              "Name": "DriveCurrent",
              "Pattern": "*_DriveCurrent",
              "Kind": "Numeric"
            },
            {
              "Name": "Speed",
              "Pattern": "*_Speed",
              "Kind": "Numeric"
            }
          ],
          "ContextSignals": [
            {
              "Name": "Mode",
              "Pattern": "*_Mode",
              "DefaultValue": "UNKNOWN",
              "MaximumAgeSeconds": 60
            },
            {
              "Name": "Load",
              "Pattern": "*_LoadType",
              "DefaultValue": "UNKNOWN",
              "MaximumAgeSeconds": 60
            },
            {
              "Name": "Route",
              "Pattern": "*_RouteId",
              "DefaultValue": "NONE",
              "MaximumAgeSeconds": 300
            }
          ],
          "PeerComparisonEnabled": true,
          "MinimumPeerDevices": 5,
          "PeerBucketWaitMs": 1000,
          "PeerBucketRetentionSeconds": 120,
          "PeerMadMultiplier": 6.0,
          "MinimumPeerMad": 0.01,
          "ConsecutivePeerAbnormalCount": 3,
          "ConsecutivePeerRecoveryCount": 5,
          "PeerSeverity": "Warning",
          "PeerRaiseAlarm": false
        }
      ]
    }
  }
}
```

## 7. 参数说明

| 参数 | 说明 | 建议 |
|---|---|---|
| ContextSignals | 运行模式、产品、负载、路线等分组信号 | 只配置真正影响基线的字段 |
| MaximumAgeSeconds | 上下文样本有效期 | 大于正常刷新周期，避免长期陈旧 |
| DefaultValue | 缺失或过期时的上下文值 | 使用明确的 UNKNOWN/NONE |
| PeerComparisonEnabled | 同群检测开关 | 默认 false，现场从 Shadow 开始 |
| MinimumPeerDevices | 最小同群设备数 | 通常不少于 5 |
| PeerBucketWaitMs | 窗口结束后等待迟到设备的时间 | 根据采集抖动设置 |
| PeerBucketRetentionSeconds | 桶和恢复状态保留时间 | 大于窗口和允许延迟 |
| PeerMadMultiplier | 正式偏离倍数 | 先 6～10，结合影子数据整定 |
| MinimumPeerMad | 稳健尺度下限 | 防止同群值完全一致时除零或过敏 |
| ConsecutivePeerAbnormalCount | 激活连续窗口数 | 通常 2～5 |
| ConsecutivePeerRecoveryCount | 恢复连续窗口数 | 通常不小于异常连续数 |
| PeerSeverity | 正式异常等级 | 先 Warning |
| PeerRaiseAlarm | 是否桥接 AlarmCenter | Shadow 阶段 false |

## 8. 状态接口

```http
GET /api/anomaly/ml/context-peer/status
```

主要指标：

- TrackedContextDevices；
- CompletedWindows；
- DroppedIncompleteWindows；
- TrackedWindows；
- BucketsEvaluated；
- DevicesEvaluated；
- Raised；
- Recovered；
- ShadowRaised；
- ActiveRaised；
- SkippedBuckets；
- Failures；
- TrackedBuckets；
- TrackedStates。

`MlLoadTest` 环境另提供专用负载接口：

```http
POST /api/anomaly/ml/context-peer/load
```

非 `MlLoadTest` 环境返回 404。

## 9. 自动化测试结果

### 9.1 单元测试

已覆盖：

- 上下文值解析和超时失效；
- 单个离群设备识别和恢复；
- 不同上下文不混合；
- Shadow 候选不发布正式生命周期；
- 上下文切换不继承连续计数；
- 上下文切换自动恢复旧上下文活动异常。

### 9.2 RawSignalEvent + SQL 专用 E2E

测试链路：

```text
RawSignalEvent
→ EventBus
→ SampleFactory
→ OperatingContextCenter
→ PeerWindow
→ Median/MAD
→ Governance / SQL
```

结果：

| 模式 | 候选 | 正式生命周期 | 恢复 | 失败 |
|---|---:|---:|---:|---:|
| Shadow | 20 | 0 | 20 个候选全部恢复 | 0 |
| Active | 20 | 20 | 20 个正式生命周期全部恢复 | 0 |
| Canary 25% | 40 | 10 | 40 个候选及 10 个正式生命周期全部恢复 | 0 |

Canary 数据守恒：

```text
40 个候选 = 10 个正式路由 + 30 个影子候选
正式 SQL 行数 = RoutedToActiveLifecycle 数量 = 10
```

### 9.3 回归矩阵

同一提交已经通过：

- ML 单元测试；
- ML 治理 E2E；
- ML 准确性 E2E；
- 模型回滚和 10 万窗口推理；
- v1 高基数精确生命周期；
- 系统端到端负载；
- Windows Core、Host、Desktop；
- 10 分钟异常生命周期、误报和内存持续测试。

一小时系统持续测试作为扩展稳定性验证独立运行。

## 10. 投产流程

建议顺序：

```text
PeerComparisonEnabled=false
→ 配置 ContextSignals 和 Signals
→ Shadow 运行
→ 人工复核候选
→ 调整上下文、MinimumPeerDevices、MAD 和连续计数
→ Canary 5%
→ Canary 25%
→ Canary 50%
→ Active
```

每次扩大比例前必须核对：

- 候选准确性；
- 不同上下文是否被正确分组；
- 上下文缺失率；
- SkippedBuckets；
- 正式异常数量；
- 设备切换工况时的恢复行为；
- SQL、内存和报警压力。

## 11. 限制

- 同群检测假设大多数设备在同一窗口内正常；若整个设备群同时异常，横向对比可能无法识别；
- 设备数量不足时应使用规则、历史基线或 Isolation Forest；
- 上下文点位错误会直接造成错误分组；
- 同群检测只提供异常证据，不替代 PLC 安全联锁；
- 当前版本不计算剩余寿命，也不自动给出机械部件根因；
- 正式启用前必须完成项目级点位、工况和阈值验收。
