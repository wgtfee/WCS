# PLC 异常检测模型使用手册

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 文档版本 | V1.0 |
| 适用版本 | `develop@1e7cda6b8d6dfd230293575edc5680bb383971e6` 及后续兼容版本 |
| 适用角色 | 实施、运维、设备、工艺、测试和授权模型管理员 |
| 前置文档 | 22-PLC异常检测模型架构说明书、23-PLC异常检测功能说明书 |

## 2. 使用前准备

### 2.1 环境要求

- .NET 8 Host 可正常启动；
- SQL Server 可用，并完成 `Wcs_PlcAnomaly` 和相关表初始化；
- PLC 点位映射和字段命名稳定；
- 服务器具备模型、训练数据和 telemetry spool 目录写权限；
- 服务器时间同步，PLC 原始时间必须可转换为 UTC；
- 已明确哪些数据属于“正常训练数据”。

### 2.2 目录规划

推荐使用应用发布目录之外的持久化路径：

```text
D:\WCSData\
├── plc-telemetry-spool\
├── anomaly-models\
└── anomaly-training\
```

Linux 示例：

```text
/var/lib/wcs/
├── plc-telemetry-spool/
├── anomaly-models/
└── anomaly-training/
```

目录要求：

- Wcs.Host 服务账号可读写；
- 普通桌面用户不可修改；
- 纳入磁盘空间、IO 和备份监控；
- 发布程序时不得清空；
- 不使用网络共享作为默认 WAL 路径。

## 3. 配置 PLC 时序存储

### 3.1 SQL Server 模式

```json
{
  "Storage": {
    "Telemetry": {
      "Provider": "SqlServer",
      "DurabilityMode": "Buffered",
      "ChannelCapacity": 100000,
      "BatchSize": 1000,
      "FlushIntervalMs": 1000,
      "RetryDelayMs": 2000,
      "SpoolDirectory": "D:/WCSData/plc-telemetry-spool"
    }
  }
}
```

适合：

- 数据量中等；
- 不单独部署 InfluxDB；
- 需要统一使用 SQL 运维体系。

### 3.2 InfluxDB 模式

```json
{
  "Storage": {
    "Telemetry": {
      "Provider": "InfluxDb",
      "DurabilityMode": "Buffered",
      "ChannelCapacity": 100000,
      "BatchSize": 1000,
      "FlushIntervalMs": 1000,
      "RetryDelayMs": 2000,
      "SpoolDirectory": "D:/WCSData/plc-telemetry-spool",
      "Site": "factory-a",
      "Measurement": "plc_signal",
      "InfluxDb": {
        "ApiVersion": "V2",
        "Url": "http://127.0.0.1:8086",
        "Token": "通过环境变量或密钥系统提供",
        "Organization": "wcs",
        "Bucket": "wcs_plc",
        "Database": "wcs_plc",
        "Gzip": false
      }
    }
  }
}
```

生产环境建议使用环境变量覆盖 Token：

```text
Storage__Telemetry__InfluxDb__Token=<token>
```

### 3.3 强耐久 WriteAhead 模式

```json
{
  "Storage": {
    "Telemetry": {
      "DurabilityMode": "WriteAhead",
      "WalBatchSize": 256,
      "WalFlushIntervalMs": 10,
      "SpoolDirectory": "D:/WCSData/plc-telemetry-spool"
    }
  }
}
```

WriteAhead 会增加磁盘 IO。启用前必须测试：

- 采样吞吐；
- 磁盘写入延迟；
- WAL 峰值空间；
- 数据库停机后的积压恢复时间；
- 服务账号目录权限。

### 3.4 状态检查

```bash
curl http://127.0.0.1:5000/api/telemetry/status
```

重点检查：

```text
dropped = 0
conservationDelta = 0
queue、walPending、spoolPending 最终可回落到 0
lastError 为空或故障恢复后不再增长
```

## 4. 启用规则异常检测 v1

### 4.1 基础配置

```json
{
  "AnomalyDetection": {
    "Enabled": true,
    "WindowSize": 120,
    "MinimumSamples": 30,
    "MaximumTrackedRuleSignals": 20000,
    "InactiveStateRetentionSeconds": 300,
    "RelatedSampleRetentionSeconds": 60,
    "MaximumCleanupItemsPerSweep": 10000,
    "ObserveThreshold": 0.70,
    "WarningThreshold": 0.85,
    "AlarmThreshold": 0.95,
    "ConsecutiveWarningCount": 3,
    "ConsecutiveAlarmCount": 5,
    "RecoveryCount": 10,
    "DurationSweepIntervalMs": 1000,
    "AlarmDelayRaiseMs": 0,
    "AlarmDelayRecoverMs": 1000,
    "Rules": []
  }
}
```

> ASP.NET Core 会按数组下标合并不同 appsettings 文件。基础 `appsettings.json` 中应保持 `Rules: []`，现场规则放在最终环境配置或独立配置源，避免示例规则字段泄漏到生产规则。

### 4.2 电流上限规则

```json
{
  "RuleId": "CV-MOTOR-CURRENT-HIGH",
  "Enabled": true,
  "PlcPattern": "PLC*",
  "DevicePattern": "CV*",
  "SignalPattern": "*_MotorCurrent",
  "Maximum": 15.0,
  "Severity": "Warning",
  "RaiseAlarm": true,
  "ConsecutiveAbnormalCount": 3,
  "ConsecutiveRecoveryCount": 5,
  "Description": "输送机电机电流持续超过15A"
}
```

### 4.3 变化率规则

```json
{
  "RuleId": "CV-CURRENT-RATE",
  "Enabled": true,
  "PlcPattern": "PLC*",
  "DevicePattern": "CV*",
  "SignalPattern": "*_MotorCurrent",
  "MaximumRatePerSecond": 5.0,
  "Severity": "Warning",
  "RaiseAlarm": false,
  "Description": "电流单位秒变化率异常"
}
```

### 4.4 Busy 持续超时规则

```json
{
  "RuleId": "DEVICE-BUSY-TIMEOUT",
  "Enabled": true,
  "PlcPattern": "*",
  "DevicePattern": "*",
  "SignalPattern": "*_Busy",
  "MaximumTrueDurationMs": 30000,
  "Severity": "Error",
  "RaiseAlarm": true,
  "Description": "设备Busy持续超过30秒"
}
```

### 4.5 Median/MAD 动态基线规则

```json
{
  "RuleId": "CV-CURRENT-MAD",
  "Enabled": true,
  "PlcPattern": "PLC*",
  "DevicePattern": "CV*",
  "SignalPattern": "*_MotorCurrent",
  "StatisticalBaselineEnabled": true,
  "MadMultiplier": 6.0,
  "MinimumMad": 0.05,
  "Severity": "Observe",
  "RaiseAlarm": false,
  "Description": "电流偏离近期稳健统计基线"
}
```

上线初期建议使用 `Observe`，积累误报结果后再升级为 Warning。

### 4.6 Running 与 Speed 一致性规则

```json
{
  "RuleId": "RUNNING-WITHOUT-SPEED",
  "Enabled": true,
  "PlcPattern": "*",
  "DevicePattern": "CV*",
  "SignalPattern": "*_Running",
  "RelatedSignalPattern": "*_Speed",
  "WhenValueEquals": "true",
  "RelatedMinimum": 0.1,
  "MaximumRelatedAgeMs": 5000,
  "Severity": "Warning",
  "RaiseAlarm": true,
  "ConsecutiveAbnormalCount": 3,
  "ConsecutiveRecoveryCount": 3,
  "Description": "运行位有效但速度反馈为零"
}
```

### 4.7 检查状态和活动异常

```bash
curl http://127.0.0.1:5000/api/anomaly/status
curl http://127.0.0.1:5000/api/anomaly/active
```

上线前应确认：

- `configuredRules` 与预期一致；
- `matchedRuleEvaluations` 持续增加；
- `failures=0`；
- 缓存数量稳定；
- 恢复后 `activeAnomalies` 能回落；
- 淘汰计数能够增长而非状态无限常驻。

## 5. 启用机器学习异常检测 v2

### 5.1 Profile 配置示例

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": true,
      "ManagementApiEnabled": false,
      "ModelDirectory": "D:/WCSData/anomaly-models",
      "TrainingDirectory": "D:/WCSData/anomaly-training",
      "MaintenanceIntervalMs": 1000,
      "MaximumTrackedWindows": 20000,
      "InactiveInferenceStateRetentionSeconds": 300,
      "Profiles": [
        {
          "ProfileId": "CV-MOTOR-CURRENT",
          "Enabled": true,
          "PlcPattern": "PLC*",
          "DevicePattern": "CV*",
          "WindowSeconds": 10,
          "MinimumSamplesPerSignal": 3,
          "CollectTrainingData": false,
          "AutoTrain": false,
          "MinimumTrainingWindows": 500,
          "MaximumTrainingWindows": 50000,
          "TreeCount": 120,
          "SampleSize": 256,
          "Contamination": 0.01,
          "RandomSeed": 20260725,
          "ObserveThreshold": 0.60,
          "WarningThreshold": 0.65,
          "AlarmThreshold": 0.85,
          "ConsecutiveAbnormalCount": 3,
          "ConsecutiveRecoveryCount": 5,
          "Severity": "Warning",
          "RaiseAlarm": false,
          "Signals": [
            {
              "Name": "MotorCurrent",
              "Pattern": "*_MotorCurrent",
              "Kind": "Numeric"
            }
          ]
        }
      ]
    }
  }
}
```

### 5.2 训练阶段

首次采集训练数据时：

1. 确认设备处于正常生产状态；
2. 暂时设置 `CollectTrainingData=true`；
3. 保持 `RaiseAlarm=false`；
4. 覆盖空载、满载、不同产品和允许的速度范围；
5. 采集数量达到 `MinimumTrainingWindows` 以上；
6. 检查训练目录中的 `features.jsonl`；
7. 结束采集后设置 `CollectTrainingData=false`；
8. 重启 Host 或通过受控配置发布应用变更。

禁止采集：

- 急停、检修、手动点动；
- 已知机械卡阻；
- PLC 通信异常；
- 传感器失效；
- 故障恢复过程；
- 不确定是否正常的运行数据。

### 5.3 临时开启管理 API

生产环境中管理 API 默认关闭。训练窗口完成并进入维护窗口后，可通过安全配置临时设置：

```text
AnomalyDetection__MachineLearning__ManagementApiEnabled=true
```

要求：

- 只在受控内网开放；
- 由反向代理、身份认证或运维网络限制访问；
- 完成训练和回滚后重新关闭；
- 记录操作人、Profile、版本、时间和原因。

### 5.4 手动训练

```bash
curl -X POST \
  http://127.0.0.1:5000/api/anomaly/ml/train/CV-MOTOR-CURRENT
```

返回内容包含：

- modelVersion；
- trainingSampleCount；
- calibrationSampleCount；
- treeCount；
- decisionThreshold；
- createdUtc。

训练完成后检查：

```bash
curl http://127.0.0.1:5000/api/anomaly/ml/status
```

确认 `activeModelVersion` 已更新且 `failures=0`。

### 5.5 查询模型版本

```bash
curl http://127.0.0.1:5000/api/anomaly/ml/models/CV-MOTOR-CURRENT
```

每个版本包含：

- version；
- createdUtc；
- trainingSampleCount；
- calibrationSampleCount；
- treeCount；
- decisionThreshold；
- isActive。

### 5.6 回滚模型

```bash
curl -X POST \
  http://127.0.0.1:5000/api/anomaly/ml/models/CV-MOTOR-CURRENT/<version>/activate
```

回滚前要求：

- 当前 Profile 无活动异常；
- 候选模型特征名称和顺序与 Profile 一致；
- 已备份当前 `active.json` 和版本文件；
- 已记录回滚原因；
- 回滚后重启验证仍加载同一版本。

返回 409 时通常表示存在活动异常或当前状态不允许切换，禁止绕过安全检查直接覆盖文件。

### 5.7 影子运行

正式报警前推荐至少经历：

```text
CollectTrainingData=false
RaiseAlarm=false
Severity=Observe 或 Warning
→ 连续观察
→ 与现场报警、维修记录和工艺数据对照
→ 统计误报/漏报
→ 调整 Profile 和阈值
→ 审批后启用 RaiseAlarm
```

影子期至少记录：

- 设备总数；
- 推理窗口数；
- 观察次数；
- 正式异常数；
- 人工确认真异常数；
- 误报数；
- 漏报数；
- 各模型版本效果。

## 6. 日常监控

### 6.1 每班检查

- `/api/telemetry/status` 数据守恒；
- `/api/anomaly/status` failures 和 activeAnomalies；
- `/api/anomaly/ml/status` 活动模型和推理失败；
- SQL `Wcs_PlcAnomaly` 活动记录；
- spool、WAL、模型和训练目录磁盘空间；
- Host RSS、GC 和日志错误。

### 6.2 推荐报警门槛

- telemetry `dropped > 0`：立即告警；
- `conservationDelta != 0` 且持续：立即调查；
- WAL/spool 长期不回落：数据库或网络告警；
- ML `failures > 0`：模型或特征错误告警；
- `DroppedIncompleteWindows` 快速增长：采样密度或时间戳问题；
- TrackedWindows 接近上限：Profile 匹配范围或 TTL 配置问题；
- 模型目录不可写：阻止训练和版本发布。

## 7. 常见问题

### 7.1 规则没有命中

检查：

- `Enabled=true`；
- PlcPattern、DevicePattern、SignalPattern；
- FieldName 能否正确提取 DeviceId；
- 数值是否使用 InvariantCulture 格式；
- 环境配置数组是否被其他 appsettings 按下标合并；
- `matchedRuleEvaluations` 是否增加。

### 7.2 模型没有推理

检查：

- `MachineLearning.Enabled=true`；
- Profile.Enabled=true；
- activeModelVersion 是否存在；
- Profile 是否匹配 PLC、Device 和 Signal；
- 每窗口样本是否达到 `MinimumSamplesPerSignal`；
- `DroppedIncompleteWindows` 是否增加。

### 7.3 正常数据误报

处理顺序：

1. 保持 `RaiseAlarm=false`；
2. 核对训练数据是否覆盖当前工况；
3. 检查产品、负载和运行模式是否混在一个 Profile；
4. 提高 `WarningThreshold`；
5. 增加 `ConsecutiveAbnormalCount`；
6. 重新采集正常数据并训练新版本；
7. 对比版本后再激活。

不要只降低模型灵敏度而忽略工况拆分。

### 7.4 模型训练失败

常见原因：

- 训练窗口不足；
- 特征维数不一致；
- 模型或训练目录无写权限；
- 有活动异常；
- Profile 不存在或已禁用；
- 训练数据文件损坏。

### 7.5 Host 重启后模型未加载

检查：

- `active.json` 是否存在且有效；
- ModelDirectory 是否指向持久化目录；
- 服务账号是否有读权限；
- ProfileId 和特征顺序是否一致；
- Host 日志中的 model load 错误。

## 8. 变更与回退

任何规则、Profile、阈值、训练集和活动模型变更必须记录：

- 变更前值；
- 变更原因；
- 影响设备；
- 训练数据时间范围；
- 模型版本；
- 测试结果；
- 审批人；
- 回退版本。

回退优先级：

1. `RaiseAlarm=false`；
2. 回滚活动模型；
3. 禁用单个 Profile；
4. `MachineLearning.Enabled=false`；
5. `AnomalyDetection.Enabled=false`；
6. 保留 telemetry 采集用于后续诊断。

关闭异常检测不应关闭 PLC 安全联锁和 WCS 核心调度保护。