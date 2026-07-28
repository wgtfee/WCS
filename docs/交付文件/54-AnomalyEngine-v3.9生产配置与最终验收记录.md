# AnomalyEngine v3.9 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | AnomalyEngine v3.9 |
| 能力 | 故障概率与剩余寿命区间预测 |
| 默认状态 | 关闭 |
| 首轮验收 Head | `89a30dc9d71c7ee004cd88e19d2326b1aba082d6` |
| 首轮矩阵 | 25/25 success |
| 当前验收级别 | 仓库级研发与 CI 验证完成，等待最终证据 Head 二次复验 |
| 现场投产状态 | 未验收 |

## 2. 生产默认配置

仓库和 `appsettings.Production.json` 必须保持：

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": false,
      "ManagementApiEnabled": false,
      "PluggableRuntime": {
        "Enabled": false,
        "Profiles": []
      },
      "Profiles": []
    }
  },
  "AssetFailureForecast": {
    "Enabled": false,
    "ModelDirectory": "data/failure-forecast-models",
    "EvaluationIntervalSeconds": 300,
    "MinimumHistoryPoints": 48,
    "MinimumHistorySpanHours": 24,
    "MaximumHistoryPoints": 2000,
    "MaximumAssetsPerEvaluation": 1000,
    "MaximumForecastsQueryCount": 1000,
    "ForecastRetentionHours": 8760,
    "MaintenanceIntervalSeconds": 3600,
    "MaintenanceBatchSize": 2000,
    "MaximumModelArtifactMegabytes": 256,
    "MinimumTrainingAssets": 30,
    "MinimumFailureEvents": 10,
    "MinimumValidationAuc": 0.65,
    "MaximumValidationBrierScore": 0.30,
    "MinimumPredictionIntervalCoverage": 0.70
  }
}
```

通用配置不得出现活动模型版本、真实模型文件、下载 URL、训练数据、许可证、站点密钥或真实资产数据。

## 3. 启用前依赖

### 3.1 健康历史

- `AnomalyHealthScoring.HistoryProvider=SqlServer`；
- 资产 ID 稳定且与设备台账一致；
- 时间戳统一为 UTC；
- 采样频率、缺失点和历史保留满足模型数据字典；
- 每个待预测资产达到项目定义的最小历史长度；
- 健康评分版本变化已评估对特征分布的影响。

### 3.2 训练数据

- 训练、验证和测试按资产拆分，禁止同一资产泄漏；
- 故障事件经过设备、维修和可靠性人员确认；
- 未故障资产按正确删失时间建模；
- 预防性维修、零件更换和工况变化有明确标签策略；
- 数据集版本不可变且可追溯；
- 资产类型、工况、季节和站点偏差已分析；
- 标签审批不会自动触发训练或激活。

### 3.3 模型与许可证

- ONNX 导出工具和运行时许可证已审核；
- 模型只使用 CPU 支持算子；
- 输入名称、输出名称、float32 类型和维度符合契约；
- Artifact SHA-256 和 ManifestHash 已独立复核；
- 模型文件发布目录具备最小权限、备份和防篡改措施。

## 4. 模型最低验收信息

Manifest 必须填写：Version、Source、ApprovedBy、ApprovedAtUtc、TrainingDatasetVersion、TrainingAssetCount、FailureEventCount、CensoredRecordCount、ValidationAuc、ValidationBrierScore、ValidationRulMaeHours、ValidationIntervalCoverage、FeatureNames、Means、StandardDeviations、Input/Output Name 与 Shape、MaximumRulHours 和 ArtifactSha256。

仓库门槛只用于拒绝明显不合格模型。项目应结合漏报成本、误报成本、维修提前量和安全风险确定正式验收值。

## 5. 输出解释

### 5.1 故障概率

24、72、168 小时概率表示模型在对应观察窗口内的估计，不表示 PLC 联锁概率、法定风险等级或故障必然发生。概率必须随时间窗口单调不减；不满足时整次输出无效。

### 5.2 RUL 区间

RUL 输出为 Lower、Median、Upper，不允许只显示单点小时数而隐藏不确定性。

- Lower：较保守的剩余寿命边界；
- Median：模型中位估计；
- Upper：较乐观的剩余寿命边界。

任何边界都不能替代设备厂家寿命、法定检验周期、PLC 联锁、点检规程或安全停机条件。

## 6. 首轮专项验收证据

### 6.1 Compile

```text
Workflow: WCS Asset Failure Forecast Compile #24
Run ID: 30374580719
Head: 89a30dc9d71c7ee004cd88e19d2326b1aba082d6
Artifact: wcs-asset-failure-forecast-compile-24
Digest: sha256:9ed5e3bd847f7f143cb641331acee651acdcb728ae3ec8594699342c0632cda3
Conclusion: success
```

### 6.2 Runtime

```text
Workflow: WCS Asset Failure Forecast Runtime #11
Run ID: 30374580859
Head: 89a30dc9d71c7ee004cd88e19d2326b1aba082d6
Artifact: wcs-asset-failure-forecast-runtime-11
Digest: sha256:db286fc938e0baba9910560bf82dc6bd81f149cf6087e271c040f1fdbb759634
Conclusion: success
Throughput: 76,747.03 predictions/s
20,000 inference RSS growth: 18,718,720 bytes（约 17.85 MB）
```

### 6.3 Host + SQL

```text
Workflow: WCS Asset Failure Forecast #9
Run ID: 30374580756
Head: 89a30dc9d71c7ee004cd88e19d2326b1aba082d6
Artifact: wcs-asset-failure-forecast-9
Digest: sha256:6125c7206eba529a327daf8f35a94f36c08f1e5f887202f9def06ec3d32bf7c9
Conclusion: success
```

专项精确验证了短历史无预测、48 点/47 小时预测门槛、v1/v2 精确输出、Forecast/Outcome 幂等、SQL 2/2/1、Brier≈0.01、RUL MAE=0、区间覆盖=1、切版、重启、坏 Hash 隔离与恢复、`Wcs_PlcWriteLog=0` 以及 Forecast 代码无控制依赖。

## 7. 首轮完整矩阵

首轮 exact Head `89a30dc9d71c7ee004cd88e19d2326b1aba082d6` 已完成 25/25：

```text
Forecast Compile #24
Forecast Runtime #11
Forecast Host+SQL #9
Adapter Compile #61
Adapter E2E #54
Adapter Host E2E #44
PLC ML #182
PLC ML E2E #174
ML Governance #135
ML Context Peer #123
ML Version Throughput #150
PLC Engine Load #286
PLC Engine Soak #269
Telemetry Storage #102
Windows CI #349
End-to-End Load #274
One Hour Soak #240
Transport Cycle #129
Health Scoring #146
Health Scoring SQL #116
Health Governance Compile #57
Health Governance #88
Root Cause #71
Maintenance Compile #65
Maintenance #74
```

该矩阵证明 v3.9 未破坏 v1～v3.8、Windows、SQL、负载和长时间稳定性基线。

## 8. 权限与审计

| 操作 | 建议角色 |
|---|---|
| 查看状态和 Forecast | 运维、可靠性、设备工程师 |
| 手工评估 | 可靠性工程师 |
| 激活模型版本 | 模型管理员 + 设备负责人审批 |
| 记录 Outcome | 有权限的设备/维修/可靠性人员 |
| 修改生产配置 | 发布管理员 |

所有写操作保存身份、时间、版本和原因。共享账号或仅依赖请求体 `RecordedBy` 不满足正式生产审计要求。

## 9. SQL 与备份

生产确认以下表和索引存在：

```text
Wcs_AssetFailureForecastModelVersion
Wcs_AssetFailureForecast
Wcs_AssetFailureForecastOutcomeJournal
```

Forecast 与 Outcome 保留周期应符合可靠性分析要求；ModelVersion 和 Manifest 审计不得因模型文件替换而丢失；备份恢复演练必须覆盖三张表和本地模型目录，并验证 active.json、ManifestHash 和 SQL 模型版本一致。

## 10. 现场试运行顺序

```text
保持 Enabled=false
→ 导入历史并做离线回放
→ 手工 Evaluate 单资产
→ 核验特征与输出
→ 记录真实 Outcome
→ 观察 Brier / RUL MAE / Interval Coverage
→ 小范围资产定时评估
→ 扩大资产范围
→ 项目级正式签署
```

不得跳过手工评估直接开启全量周期预测。

## 11. 监控指标

至少监控 Availability、ActiveModelVersion、ManifestHash、ArtifactSha256、EvaluationAttempts、ForecastsCreated、InsufficientData、Failures、LastError、SQL 写入延迟、24h Brier、RUL MAE、Prediction Interval Coverage、分资产类型/工况/站点指标，以及模型目录容量和文件变更。

## 12. 回退

第一优先级：

```text
AssetFailureForecast__Enabled=false
```

回退后停止新 Forecast 和周期评估，保留模型、Manifest、Forecast 和 Outcome；不影响 v3.4 健康历史、v3.5～v3.8、PLC、任务、路权或调度。

## 13. 仓库级验收清单

- [x] 固定 14 维 FeatureSchema；
- [x] 历史点数和跨度不足时无预测；
- [x] 已审批本地 Manifest 与 SHA 校验；
- [x] 训练资产、真实故障、删失与验证指标门槛；
- [x] 真实 CPU ONNX Runtime；
- [x] 24/72/168 小时概率；
- [x] RUL Lower/Median/Upper；
- [x] 概率单调与区间有序校验；
- [x] SQL 模型、Forecast 与 Outcome 三表；
- [x] Forecast 和 Outcome 幂等；
- [x] Brier、RUL MAE 和区间覆盖；
- [x] 模型切换和 Host 重启恢复；
- [x] 坏 Hash 隔离；
- [x] Production 默认关闭；
- [x] 无 PLC 写入或控制依赖；
- [x] 首轮 latest exact head Forecast 三专项成功；
- [x] 首轮 latest exact head 25/25 成功；
- [ ] 最终证据 Head 二次 25/25 成功；
- [ ] PR #32 Squash 合入 `develop`。

## 14. 项目级未完成事项

以下不能由仓库 CI 替代：真实模型和数据许可证、真实资产故障与删失数据质量、模型准确率/校准/区间覆盖现场验收、设备类型与工况外推风险、身份认证和角色权限、维修策略/SOP/工单契约、现场资源容量和高可用，以及法规、安全和正式投产签署。

## 15. 当前结论

v3.9 的软件安全边界、模型治理、SQL 审计、回测接口和首轮 25/25 仓库矩阵已经完成。当前仍不得宣称真实故障概率或 RUL 达到生产准确率。

本证据更新会产生新的 latest Head。只有该 Head 再次完成同等 25/25，PR #32 才可 Ready 并 Squash 合入 `develop`；Merge SHA 将在最终合并后补入或由合并记录作为最终凭证。