# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断与人工辅助决策路线。统一调度主线第一至第十二阶段已经结束；AnomalyEngine 不得绕过状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

## 2. 已完成和当前基线

| 版本 | 能力 | 状态 |
|---|---|---|
| v3.4 | 健康评分、趋势、SQL 历史 | 已完成 |
| v3.5 | 健康事件治理、MES Outbox、审计 | 已完成，`develop@ed74768a6338aff5a1c63409ab7fb0e344ca701c` |
| v3.6 | 根因关联、传播路径、人工复核 | 已完成，`develop@900353ef8f17b3ab38cb4e711529c3fcc3629892` |
| v3.7 | 维修建议、反馈、工单、指标和标签 | 已完成，`develop@f26cdbeae77c4e246477ea0c6ba60c3511ef86f4` |
| v3.8 | 可插拔模型、本地 ONNX Runtime、Profile 分流和失败隔离 | 已完成，`develop@5e875afaf3ebee7c386dec8043b902f704dd7930` |
| v3.9 | 故障概率、RUL 区间、Outcome 回测和 SQL 审计 | 功能、专项、文档和首轮 25/25 已完成，等待最终证据 Head 二次复验与合并 |

## 3. 总体数据流

```text
规则 / 统计 / Isolation Forest / ONNX / 同群 / 周期异常
                           ↓
                   v3.3 Evidence Fusion
                           ↓
                v3.4 Health Score + Trend
                           ↓
         v3.5 Governed Health Event + MES Outbox
                           ↓
         v3.6 Root Cause Graph + Review Journal
                           ↓
         v3.7 Maintenance Rules + Feedback Journal

v3.4 retained Health History
                           ↓
         v3.9 Failure Probability + RUL Interval
                           ↓
           Forecast SQL + Outcome Journal + Metrics
```

v3.8 是 ML Evidence 入口扩展；v3.9 是健康历史的预测分析分支。二者均不位于 PLC 或调度控制链路。

## 4. v3.5～v3.8 摘要

- v3.5：健康事件创建/恢复、SQL Journal、MES Outbox、指数退避、DeadLetter 和重启恢复；
- v3.6：审批图、GraphHash、有界关联、传播路径、不可变快照、复核 Journal；
- v3.7：审批 RuleSet、RuleSetHash、维修建议、反馈 Journal、MES 工单字段、指标、候选训练标签；
- v3.8：Manifest、FeatureSchema、Adapter、本地 ONNX、Profile 物理分流、Shadow/Canary/Active、切版和失败隔离。

这些阶段均为诊断与人工辅助决策，不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单。

## 5. v3.8：可插拔模型与本地 ONNX 运行时

v3.8 已完成最终两轮 20/20 矩阵并合入 `develop@5e875afaf3ebee7c386dec8043b902f704dd7930`。

主要能力包括确定性 ManifestHash、Profile→FeatureSchema 精确映射、Adapter Registry、本地 External Model Store、Isolation Forest 与 ONNX Runtime CPU Adapter、路径 containment、256 MB 上限、SHA-256、Profile 物理分流、外部 Profile 禁止在线训练、Shadow/Canary/Active、Candidate SQL、活动异常期间禁止切版、重启恢复、坏 Hash 隔离和默认关闭。

## 6. v3.9：故障概率与剩余寿命预测

### 6.1 设计原则

v3.9 不使用健康分线性外推，不在数据或模型不足时输出虚假小时数。只有已审批本地模型和资产历史同时满足门槛时，才输出：

```text
FailureProbability24Hours
FailureProbability72Hours
FailureProbability168Hours
RulLowerHours
RulMedianHours
RulUpperHours
```

不满足条件时返回 `Disabled`、`ModelUnavailable`、`InsufficientData` 或 `Failed`，且不写 Forecast SQL。

### 6.2 固定 14 维历史特征

```text
health.latest
health.mean
health.minimum
health.maximum
health.stddev
health.slopePerHour
health.delta
fusionRisk.mean
fusionRisk.maximum
grade.changeCount
grade.degradedOrWorseRatio
grade.criticalRatio
history.sampleCount
history.spanHours
```

默认至少 48 个点和 24 小时跨度。Manifest、标准化数组和 ONNX 输入顺序必须完全一致。

### 6.3 模型治理

`AssetFailureForecastModelManifest` 要求 Version、ArtifactFile、ArtifactSha256、Source、ApprovedBy、ApprovedAtUtc、TrainingDatasetVersion、TrainingAssetCount、FailureEventCount、CensoredRecordCount、ValidationAuc、ValidationBrierScore、ValidationRulMaeHours、ValidationIntervalCoverage、输入输出名称、Shape、FeatureNames 和 MaximumRulHours。

软件最低门槛包括训练资产 ≥30、真实失效 ≥10、删失记录 ≥1、AUC ≥0.65、Brier ≤0.30、区间覆盖 ≥0.70。现场必须建立更严格标准。

### 6.4 输出约束

- 概率必须在 `[0,1]`；
- `P24 <= P72 <= P168`；
- RUL 必须有限、非负且不超过 MaximumRulHours；
- `Lower <= Median <= Upper`；
- 任何输出不合法则整次预测失败且不写 SQL。

### 6.5 SQL 与 Outcome

```text
Wcs_AssetFailureForecastModelVersion
Wcs_AssetFailureForecast
Wcs_AssetFailureForecastOutcomeJournal
```

ForecastId 按资产、模型版本和历史窗口确定性生成。Outcome 只追加：ObservedFailure、PreventiveMaintenance、CensoredNoFailure、InvalidPrediction。

Outcome 支持计算 24h Brier、RUL 中位数绝对误差和预测区间覆盖率；无 Outcome 时不伪造指标。

### 6.6 首轮专项证据

```text
Exact Head: 89a30dc9d71c7ee004cd88e19d2326b1aba082d6
Matrix: 25/25 success

Compile #24 / Run 30374580719
Artifact: wcs-asset-failure-forecast-compile-24
Digest: sha256:9ed5e3bd847f7f143cb641331acee651acdcb728ae3ec8594699342c0632cda3

Runtime #11 / Run 30374580859
Artifact: wcs-asset-failure-forecast-runtime-11
Digest: sha256:db286fc938e0baba9910560bf82dc6bd81f149cf6087e271c040f1fdbb759634

Host+SQL #9 / Run 30374580756
Artifact: wcs-asset-failure-forecast-9
Digest: sha256:6125c7206eba529a327daf8f35a94f36c08f1e5f887202f9def06ec3d32bf7c9
```

Runtime #11 验证 20,000 次推理约 76,747 次/秒、RSS 增长约 17.85 MB、Hash/Shape/FeatureOrder/弱训练证据拒绝、重启恢复和控制写入为 0。

Host+SQL #9 验证短历史无预测、48 点/47 小时门槛、v1/v2 精确输出、Forecast/Outcome 幂等、SQL 2/2/1、Brier≈0.01、RUL MAE=0、区间覆盖=1、切版、重启、坏 Hash 隔离与恢复、PLC 写入为 0 和源码无控制依赖。

### 6.7 完成门槛

- [x] Core 数据充分性与数学约束；
- [x] 本地模型、SHA、审批和训练证据；
- [x] CPU ONNX 六输出 Runtime；
- [x] SQL 三表、Forecast/Outcome 幂等和指标；
- [x] 模型切换、重启和坏模型隔离；
- [x] Production 默认关闭；
- [x] 文档 52～54；
- [x] 首轮 latest exact head Forecast Compile/Runtime/Host 全绿；
- [x] 首轮 latest exact head 25/25 全绿；
- [ ] 最终证据 Head 二次 25/25 全绿；
- [ ] PR #32 Ready 并 Squash 合入 `develop`。

### 6.8 明确边界

- 预测是诊断估计，不是故障必然性、厂家寿命或安全联锁；
- 训练标签 Approved 不自动训练或激活模型；
- 仓库不保存真实模型、数据、许可证、URL 或密钥；
- 模型、SQL 或指标失败不阻塞 Host 和控制链路；
- 不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单；
- 真实模型准确率、数据质量、维修策略、权限和投产属于项目级验收。

## 7. 执行顺序

```text
v3.5 已完成并合入 develop
  ↓
v3.6 已完成并合入 develop
  ↓
v3.7 已完成并合入 develop
  ↓
v3.8 已完成并合入 develop
  ↓
v3.9 首轮 25/25 已完成，等待最终证据 Head 二次复验与合并
```

## 8. 统一安全原则

1. 默认关闭；
2. 输入、容量、窗口、队列、查询、模型大小和保留期有界；
3. 数据或模型不足时不生成伪结果；
4. 结果可解释、可版本化、可审计、可回退；
5. SQL、MES 或模型故障不阻塞控制链路；
6. 人工操作保存身份、原因和时间；
7. 仓库不保存现场密钥、真实点位、维修规则、真实模型和未脱敏数据；
8. 不直接写 PLC、不自动停机、不修改调度；
9. 自动控制联动必须另立安全项目、风险分析和现场验收。