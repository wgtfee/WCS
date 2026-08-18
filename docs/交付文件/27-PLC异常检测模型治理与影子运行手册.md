# PLC 异常检测模型治理与影子运行手册

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 系统 | WCS Runtime Engine |
| 能力 | AnomalyEngine v2.1 模型生产治理 |
| 文档版本 | V1.0 |
| 功能基线 | `develop@8410fd82629b02f24b50ec05dc1a186ae6379d4c` |
| 文档状态 | 软件研发交付版 |
| 适用对象 | 架构、研发、测试、实施、运维、设备、工艺与模型审核人员 |

## 2. 目标

AnomalyEngine v2 已具备 Isolation Forest 训练、保存、加载、推理和回滚能力。v2.1 在其外部增加生产治理能力，解决以下问题：

1. 新模型不能未经观察直接报警；
2. 训练数据必须冻结并可追溯；
3. 模型训练申请与批准必须分离；
4. 模型候选异常需要人工判断真伪；
5. 模型效果必须形成可查询指标；
6. 在线数据分布偏移必须可观测；
7. 新模型应先影子运行，再灰度，最后全量；
8. 已有活动异常时不得切换模型；
9. 在线故障窗口不能默认污染正常训练池。

本阶段不实现深度学习，也不替代 PLC 安全联锁、急停、保护和人工维修决策。

## 3. 总体架构

```text
RawSignalEvent
    ↓
PlcAnomalySampleFactory
    ↓
PlcFeatureWindowEngine
    ↓
Active Isolation Forest Model
    ↓
Score + Drift Window
    ↓
连续异常/恢复状态机
    ├── Shadow Candidate → Wcs_PlcMlCandidate
    ├── Canary Candidate → 治理表 + 部分正式生命周期
    └── Active Candidate → 治理表 + Wcs_PlcAnomaly + AlarmCenter

训练侧：
features.jsonl
    ↓ 冻结
versioned dataset.jsonl + metadata.json
    ↓ 训练申请
model-<version>.json（Pending）
    ↓ 独立审核
Approved
    ↓ 原子激活
active.json
```

## 4. 发布模式

`PlcMlDeploymentMode` 支持四种模式：

| 模式 | 数值 | 行为 |
|---|---:|---|
| Disabled | 0 | 不执行该 Profile 的在线推理 |
| Shadow | 1 | 生成治理候选，不进入正式异常生命周期和 AlarmCenter |
| Canary | 2 | 确定性选择部分设备进入正式生命周期，其余仍为影子候选 |
| Active | 3 | 所有正式候选进入 `Wcs_PlcAnomaly`，并按 `RaiseAlarm` 决定是否桥接 AlarmCenter |

生产默认建议使用 `Shadow`。禁止新模型从未验证状态直接切换到 `Active`。

## 5. Canary 路由

Canary 不是每个请求随机抽样，而是根据以下稳定键进行确定性分桶：

```text
SHA256(ProfileId + "|" + DeviceId) % 100
```

当分桶值小于 `CanaryPercentage` 时，该设备进入正式生命周期。

特性：

- 同一 Profile、同一设备在重启后仍保持相同路由；
- 不会同一设备时而影子、时而正式；
- 调整百分比时设备集合按稳定分桶扩展或收缩；
- 设备编号改变会被视为新设备。

推荐灰度顺序：

```text
Shadow
→ Canary 5%
→ Canary 10%
→ Canary 25%
→ Canary 50%
→ Active
```

每一步都必须观察误报、漏报、活动候选、漂移和资源使用。

## 6. 训练数据治理

### 6.1 在线采集文件

默认路径：

```text
data/anomaly-training/<ProfileId>/features.jsonl
```

该文件是在线正常窗口采集池，不应直接作为不可变审计证据。

### 6.2 冻结数据集

冻结后生成：

```text
data/anomaly-training/<ProfileId>/datasets/
├── dataset-<version>.jsonl
└── dataset-<version>.json
```

元数据至少包含：

- ProfileId；
- 数据集版本；
- 创建时间；
- 窗口数量；
- 特征名称哈希；
- 创建人；
- 描述；
- 冻结状态。

冻结数据集采用临时文件、WriteThrough、显式刷盘和原子重命名，生成后不应被覆盖。

### 6.3 防训练污染

配置：

```json
{
  "CollectTrainingData": true,
  "CollectTrainingDataWhileModelActive": false
}
```

默认规则：

- 没有活动模型时，可以采集初始正常基线；
- 活动模型存在后，默认停止向正常训练池追加；
- 异常、恢复、故障窗口不会自动成为下一版正常训练数据；
- 只有受控重新采集窗口才可临时设为 `true`；
- 重新采集完成后必须恢复为 `false` 并重新冻结数据集。

## 7. 模型审批

### 7.1 状态

| 状态 | 数值 | 说明 |
|---|---:|---|
| Pending | 0 | 已训练并保存，但未允许激活 |
| Approved | 1 | 已由独立审核人批准 |
| Rejected | 2 | 被拒绝，必须重新训练生成新版本 |

### 7.2 双人原则

系统强制：

```text
RequestedBy != ApprovedBy
```

比较不区分大小写。同一账号不能训练申请并批准自己的模型。

拒绝后的模型不能直接改为批准，必须基于确认后的数据重新训练，生成新模型版本。

### 7.3 激活约束

激活前检查：

1. Profile 存在且启用；
2. 模型文件存在；
3. ProfileId 与模型一致；
4. 特征名称和顺序一致；
5. 标准化维度一致；
6. 模型已批准；
7. 当前没有活动异常候选；
8. 候选模型验证通过后才原子替换 `active.json`。

## 8. 人工复核

候选复核结果：

| 结果 | 含义 |
|---|---|
| Unreviewed | 尚未判断 |
| TruePositive | 确认为真实异常 |
| FalsePositive | 模型误报 |
| ExpectedBehavior | 偏离正常基线，但属于已知正常工况 |
| NeedsInvestigation | 尚不能确认，需要设备、工艺或维修人员调查 |

复核记录包含：

- CandidateId；
- 模型版本；
- ProfileId；
- 设备和窗口；
- 分数与阈值；
- 解释；
- 是否进入正式生命周期；
- 复核人、时间和意见。

禁止只修改统计结果而不保留候选原始记录。

## 9. 效果评估

当前自动汇总：

- 候选总数；
- 已复核数量；
- True Positive；
- False Positive；
- Expected Behavior；
- Needs Investigation；
- 未复核数量；
- Precision。

当前 Precision 定义：

```text
TruePositive / (TruePositive + FalsePositive)
```

注意：仅凭候选复核无法计算完整 Recall。Recall 需要额外提供现场已知异常、维修记录或人工抽检中的漏报数据。

## 10. 漂移监控

### 10.1 基线

训练时由独立校准集保存：

- `CalibrationMeanScore`；
- `CalibrationP95Score`。

### 10.2 在线窗口

Profile 使用滚动分数窗口计算：

- 当前平均分；
- 当前 P95；
- 与校准平均分的相对增幅；
- 与校准 P95 的相对增幅；
- 两者最大值作为 DriftRatio。

### 10.3 状态

| 状态 | 判断 |
|---|---|
| Unknown | 样本不足 |
| Stable | DriftRatio 小于 Warning 阈值 |
| Warning | 达到 Warning，未达到 Critical |
| Critical | 达到 Critical |

漂移不等于故障。可能原因包括：

- 产品或负载变化；
- 设备老化；
- PLC 标定变化；
- 工艺参数变化；
- 采样频率变化；
- 训练数据覆盖不足。

Critical 时不应自动重新训练并上线，应先调查数据和工况。

## 11. 配置示例

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": true,
      "ManagementApiEnabled": false,
      "ModelDirectory": "data/anomaly-models",
      "TrainingDirectory": "data/anomaly-training",
      "MaintenanceIntervalMs": 1000,
      "MaximumTrackedWindows": 20000,
      "InactiveInferenceStateRetentionSeconds": 300,
      "Profiles": [
        {
          "ProfileId": "CV-MOTOR-CURRENT",
          "Enabled": true,
          "PlcPattern": "PLC-*",
          "DevicePattern": "CV*",
          "WindowSeconds": 10,
          "MinimumSamplesPerSignal": 3,
          "CollectTrainingData": true,
          "CollectTrainingDataWhileModelActive": false,
          "AutoTrain": false,
          "MinimumTrainingWindows": 1000,
          "MaximumTrainingWindows": 50000,
          "TreeCount": 120,
          "SampleSize": 256,
          "Contamination": 0.01,
          "RandomSeed": 20260725,
          "ObserveThreshold": 0.60,
          "WarningThreshold": 0.65,
          "AlarmThreshold": 0.80,
          "ConsecutiveAbnormalCount": 3,
          "ConsecutiveRecoveryCount": 5,
          "Severity": "Warning",
          "RaiseAlarm": false,
          "DeploymentMode": "Shadow",
          "CanaryPercentage": 0,
          "RequireModelApproval": true,
          "DriftWindowSize": 500,
          "MinimumDriftSamples": 100,
          "DriftWarningRatio": 0.15,
          "DriftCriticalRatio": 0.30,
          "DriftSnapshotIntervalSeconds": 60,
          "Signals": [
            {
              "Name": "Current",
              "Pattern": "*_Current",
              "Kind": "Numeric"
            }
          ]
        }
      ]
    }
  }
}
```

## 12. 管理 API

管理 API 仅在以下配置为 true 时可见：

```json
"ManagementApiEnabled": true
```

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/api/anomaly/ml/governance/datasets/{profileId}` | 冻结数据集 |
| GET | `/api/anomaly/ml/governance/datasets/{profileId}` | 查询数据集 |
| POST | `/api/anomaly/ml/train/{profileId}?datasetVersion=...&requestedBy=...` | 按冻结数据集训练 |
| GET | `/api/anomaly/ml/governance/models/{profileId}` | 查询审批记录 |
| POST | `/api/anomaly/ml/governance/models/{profileId}/{version}/approve` | 批准，可选立即激活 |
| POST | `/api/anomaly/ml/governance/models/{profileId}/{version}/reject` | 拒绝模型 |
| GET | `/api/anomaly/ml/governance/candidates` | 查询候选 |
| POST | `/api/anomaly/ml/governance/candidates/{candidateId}/review` | 人工复核 |
| GET | `/api/anomaly/ml/governance/evaluation/{profileId}` | 查询效果统计 |
| GET | `/api/anomaly/ml/governance/drift/{profileId}` | 查询最新漂移 |
| GET | `/api/anomaly/ml/status` | 查询在线状态 |

当前代码使用独立功能开关控制管理 API，但项目尚未在该 Controller 上单独声明角色授权策略。生产开放前必须由部署层或项目身份模块增加认证、角色、网络隔离和审计访问控制，不能仅依赖 `ManagementApiEnabled`。

## 13. SQL 对象

| 表 | 用途 |
|---|---|
| `Wcs_PlcMlCandidate` | 影子、灰度和正式候选及人工复核 |
| `Wcs_PlcMlModelGovernance` | 训练申请、数据集版本、审批和拒绝记录 |
| `Wcs_PlcMlDriftSnapshot` | 在线分数漂移快照 |
| `Wcs_PlcAnomaly` | 仅正式路由后的异常生命周期 |

当 ML 与管理 API 均关闭时，治理 Schema 服务不会启动，也不会因该能力自动创建上述三张治理表。

## 14. 推荐实施流程

```text
确认 Profile 和信号
→ 仅采集已确认正常数据
→ 冻结数据集
→ 训练 Pending 模型
→ 独立审核
→ 批准但先 Shadow
→ 人工复核候选
→ 检查误报、漏报和漂移
→ Canary 5%～25%
→ 扩大 Canary
→ Active
```

每一步都必须保存配置版本、模型版本、数据集版本、操作者和结果。

## 15. 回退

出现以下任一情况应停止扩大灰度：

- False Positive 超过项目门槛；
- 漏报或已知故障未识别；
- Drift Critical；
- 正式异常风暴；
- CPU、内存、SQL 或文件 IO 超限；
- 模型解释与现场现象明显不一致；
- 特征采样或单位发生变化。

回退顺序：

```text
Active / Canary
→ Shadow
→ 必要时 MachineLearning.Enabled=false
→ 保留规则/统计引擎
→ 调查数据和模型
→ 冻结新数据集并训练新版本
```

## 16. 验收边界

软件验收证明治理机制、持久化、审批、影子和灰度分流按设计工作。

现场投产仍需：

- 真实 PLC 与设备数据；
- 工况覆盖；
- 现场误报和漏报门槛；
- 维修记录关联；
- 角色权限；
- 网络与文件目录安全；
- 现场联合签署。
