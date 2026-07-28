# AnomalyEngine v3.9 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | AnomalyEngine v3.9 |
| 能力 | 故障概率与剩余寿命区间预测 |
| 默认状态 | 关闭 |
| 当前验收级别 | 仓库级研发与 CI 验证 |
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

Manifest 必须填写：

- Version；
- Source；
- ApprovedBy / ApprovedAtUtc；
- TrainingDatasetVersion；
- TrainingAssetCount；
- FailureEventCount；
- CensoredRecordCount；
- ValidationAuc；
- ValidationBrierScore；
- ValidationRulMaeHours；
- ValidationIntervalCoverage；
- FeatureNames、Means、StandardDeviations；
- Input/Output Name 与 Shape；
- MaximumRulHours；
- ArtifactSha256。

仓库门槛只用于拒绝明显不合格模型。项目应结合漏报成本、误报成本、维修提前量和安全风险确定正式验收值。

## 5. 输出解释

### 5.1 故障概率

24、72、168 小时概率表示模型在对应观察窗口内的估计，不表示 PLC 联锁概率、法定风险等级或故障必然发生。

概率必须随时间窗口单调不减。若不满足，整次输出无效。

### 5.2 RUL 区间

RUL 输出为 Lower、Median、Upper，不允许只显示单点小时数而隐藏不确定性。

- Lower：较保守的剩余寿命边界；
- Median：模型中位估计；
- Upper：较乐观的剩余寿命边界。

任何边界都不能替代设备厂家寿命、法定检验周期、PLC 联锁、点检规程或安全停机条件。

## 6. 权限与审计

生产必须区分：

| 操作 | 建议角色 |
|---|---|
| 查看状态和 Forecast | 运维、可靠性、设备工程师 |
| 手工评估 | 可靠性工程师 |
| 激活模型版本 | 模型管理员 + 设备负责人审批 |
| 记录 Outcome | 有权限的设备/维修/可靠性人员 |
| 修改生产配置 | 发布管理员 |

所有写操作保存身份、时间、版本和原因。共享账号或仅依赖请求体 `RecordedBy` 不满足正式生产审计要求。

## 7. SQL 与备份

生产确认以下表和索引存在：

```text
Wcs_AssetFailureForecastModelVersion
Wcs_AssetFailureForecast
Wcs_AssetFailureForecastOutcomeJournal
```

备份和保留要求：

- Forecast 与 Outcome 保留周期符合维修和可靠性分析要求；
- 清理按小批次执行；
- ModelVersion 和 Manifest 审计不得因模型文件替换而丢失；
- 备份恢复演练包含三张表和本地模型目录；
- 恢复后验证 active.json、ManifestHash 和 SQL 模型版本一致。

## 8. 现场试运行顺序

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

## 9. 监控指标

至少监控：

- Availability；
- ActiveModelVersion、ManifestHash、ArtifactSha256；
- EvaluationAttempts；
- ForecastsCreated；
- InsufficientData；
- Failures 和 LastError；
- SQL 写入延迟和失败；
- 24h Brier；
- RUL MAE；
- Prediction Interval Coverage；
- 不同资产类型、工况和站点的分组指标；
- 模型目录容量和文件变更。

## 10. 回退

第一优先级：

```text
AssetFailureForecast__Enabled=false
```

回退后：

- 停止新 Forecast；
- 停止周期评估；
- 保留模型、Manifest、Forecast 和 Outcome；
- 不影响 v3.4 健康历史；
- 不影响 v3.5～v3.8；
- 不影响 PLC、任务、路权或调度。

模型版本回退必须先验证目标 Manifest、SHA、数据集和审批信息，再调用模型激活 API。

## 11. 仓库级验收清单

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
- [ ] latest exact head Forecast Host + SQL 全步骤成功；
- [ ] latest exact head 完整矩阵成功；
- [ ] 最终证据 Head 二次完整矩阵成功；
- [ ] PR #32 Squash 合入 `develop`。

## 12. 项目级未完成事项

以下不能由仓库 CI 替代：

- 真实模型和数据许可证；
- 真实资产故障与删失数据质量；
- 模型准确率、校准和区间覆盖现场验收；
- 设备类型与工况外推风险；
- 身份认证和角色权限；
- 维修策略、SOP 和工单契约；
- 现场资源容量和高可用；
- 法规、安全和正式投产签署。

## 13. 当前结论

v3.9 的软件安全边界、模型治理、SQL 审计和回测接口已经建立。当前不得宣称真实故障概率或 RUL 已达到生产准确率。最终 exact head、专项运行号、Artifact、Digest、完整矩阵和 Merge SHA 在最终收口后回填。
