# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断与人工辅助决策路线。

```text
EMS/RGV 统一调度主线：第一阶段～第十二阶段，软件研发已经结束
AnomalyEngine 诊断扩展线：v1～v3.9，继续以只读诊断和人工辅助决策演进
```

统一调度后续不再使用“第十三阶段”扩展。AnomalyEngine 不得绕过既有状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

## 2. 已完成和当前基线

| 版本 | 能力 | 状态 |
|---|---|---|
| v1 | 规则、阈值、变化率、持续时间、MAD、一致性 | 已完成 |
| v2 | 特征窗口与 Isolation Forest | 已完成 |
| v2.1 | 模型治理、Shadow、Canary、审批、漂移与回滚 | 已完成 |
| v3.1 | 运行上下文与同群偏离 | 已完成 |
| v3.2 | 运输动作周期与阶段异常 | 已完成 |
| v3.3 | 多模型异常证据融合 | 已完成 |
| v3.4 | 可解释健康评分、趋势、SQL 历史与重启恢复 | 已完成，`develop@1d060983671d204fb25e4bf21d5b4bebd2596153` |
| v3.5 | 健康事件治理、MES Outbox、审计和重启恢复 | 已完成，`develop@ed74768a6338aff5a1c63409ab7fb0e344ca701c` |
| v3.6 | 根因关联、传播路径、SQL 快照和人工复核 | 已完成，`develop@900353ef8f17b3ab38cb4e711529c3fcc3629892` |
| v3.7 | 维修建议、反馈闭环、MES 工单字段、指标和标签候选 | 功能、专项和首轮 19 项矩阵已完成，等待最终证据 Head 复验与合并 |

## 3. 总体数据流

```text
规则 / 统计 / ML / 同群 / 周期异常
                 ↓
v3.3 Evidence Fusion
                 ↓
v3.4 Health Score + Trend
                 ↓
v3.5 Governed Health Event + MES Outbox
                 ↓
v3.6 Root Cause Graph Analysis + Review Journal
                 ↓
v3.7 Approved Maintenance Rules + Feedback Journal
                 ↓
v3.8 Pluggable Model / ONNX Runtime
                 ↓
v3.9 Failure Probability and RUL
```

v3.5～v3.9 均属于诊断与辅助决策层。自动停机、自动修改调度、自动释放路权或自动下发维修动作必须进入独立安全项目。

## 4. v3.5：健康事件治理与 MES 联动

已交付事件创建/恢复、同一资产单活动事件、SQL Journal 双重幂等、MES HTTP Outbox、指数退避、DeadLetter、人工重试和 Host 重启恢复。SQL、MES 和网络故障不阻塞 PLC、任务和调度。

边界：不停止设备、不取消任务、不修改车辆、路径或路权、不替代 AlarmCenter、不把 MES 响应当作 PLC 联锁。

## 5. v3.6：根因关联与异常传播分析

已交付版本化审批图、GraphHash、图校验、有界时间窗口、上游搜索、最短传播路径、可解释排序、确定性 AnalysisId、SQL 不可变分析快照、人工复核 Journal 和 Host 重启恢复。

```text
Merge SHA: 900353ef8f17b3ab38cb4e711529c3fcc3629892
Specialty: WCS Asset Health Root Cause #9
Artifact: wcs-asset-health-root-cause-9
Digest: sha256:44688aa44d8710b24c1372b9dcf0dccc53f9e7165e94353d7ed034f8860194eb
```

边界：Confidence 不是故障概率；无图映射不猜测；Supplemented 不修改活动图；图修改必须新版本审批；不自动停机、取消任务或修改调度。

## 6. v3.7：维修决策支持与反馈闭环

### 6.1 已实现能力

- 已审批维修 RuleSet，包含 Version、Source、ApprovedBy、ApprovedAtUtc；
- SHA-256 RuleSetHash，同版本不同 Hash 拒绝；
- 根因到检查项、部件、工具、备件和安全注意事项的规则映射；
- Confirmed 或 Supplemented 后才允许生成建议；
- Confirmed 受 `MinimumRootCauseConfidence` 限制；
- 精确 RootCauseNodeId 规则优先于 RootCauseKind 规则；
- 无匹配规则时不生成伪建议；
- 确定性 RecommendationId 和 SQL 幂等；
- Proposed、Accepted、Rejected、Completed、Cancelled 生命周期；
- Accepted、Rejected、FalsePositive、Repaired、NoFaultFound、Cancelled 反馈；
- MES 工单号、处理人和完成时间审计字段；
- 维修前后健康分；
- 接受率、确认故障率、误报率和平均关闭时长；
- Repaired、FalsePositive、NoFaultFound 形成候选训练标签；
- 训练标签 PendingApproval、Approved、Rejected；
- Host 重启恢复建议、反馈和标签。

### 6.2 SQL 对象

```text
Wcs_AssetHealthMaintenanceRuleSetVersion
Wcs_AssetHealthMaintenanceRecommendation
Wcs_AssetHealthMaintenanceFeedbackJournal
Wcs_AssetHealthMaintenanceTrainingLabel
```

反馈只更新建议生命周期摘要并追加 Journal，不覆盖原始 RecommendationJson、v3.5 事件或 v3.6 分析。

### 6.3 专项与首轮完整矩阵

```text
Workflow: WCS Asset Health Maintenance #9
Run ID: 30345256689
Source SHA: 2ab1f05a2c8fccb3b9e273c48ab6b51d08e3c542
Artifact: wcs-asset-health-maintenance-9
Digest: sha256:26e1269d6057a0df3ca2a75be1191ccc771293bc09f53718b8a8cf459339a39b
Conclusion: success
```

专项断言：RuleSet=1、Recommendation=1、Feedback=2、TrainingLabel=1；重复建议和反馈幂等；Accepted→Repaired；MES-WO-1001；PostHealthScore=92；`fault-confirmed` 标签人工 Approved；Host 重启恢复。

首轮完整矩阵：

```text
Exact Head: 2ab1f05a2c8fccb3b9e273c48ab6b51d08e3c542
Result: 19/19 success
```

覆盖 Maintenance Compile/E2E、Root Cause、Governance Compile/E2E、Health Scoring/SQL、Windows CI、End-to-End Load、Telemetry、PLC Anomaly Load/Soak、ML/E2E/Governance/Context/Version、Transport Cycle 和 One Hour Soak。

本证据文档提交形成的新 Head 必须再次通过同等矩阵，旧 Head 成功不得替代最终复验。

### 6.4 完成门槛

- Core 规则和安全边界测试通过；
- SQL 四表、索引和精确计数通过；
- 建议/反馈/标签幂等通过；
- MES 工单字段和指标通过；
- Host 重启恢复通过；
- Production 默认关闭、规则集为空；
- 最新源码与文档 Head 完整回归和一小时 Soak 全绿；
- 文档 21、39、46～48 完整；
- PR #30 Ready 并合入 `develop`。

### 6.5 明确边界

- 建议是检查建议，不是控制命令；
- MES 工单号只是审计字段；
- 无规则不生成建议；
- 训练标签 Approved 不自动训练、激活或替换模型；
- 不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单；
- 真实维修 SOP、权限、MES 契约和正式投产属于项目级验收。

## 7. v3.8：可插拔模型与 ONNX 运行时

保留纯 .NET Isolation Forest，同时建立统一模型适配边界，使本地 ONNX 模型复用 Profile、治理、Shadow、Canary、回滚和 Evidence Fusion。仅在存在经过现场验收的本地模型、稳定特征和明确许可证时实施。

## 8. v3.9：故障概率与剩余寿命预测

在具备长期退化数据、真实故障和维修记录后，输出部件未来故障概率、剩余寿命区间和建议维护窗口。数据不足时保持关闭，不输出虚假 RUL。

## 9. 执行顺序

```text
v3.5 已完成并合入 develop
  ↓
v3.6 已完成并合入 develop
  ↓
v3.7 首轮矩阵已完成，等待最终证据 Head 复验与合并
  ↓
v3.8 仅在成熟本地模型存在时实施
  ↓
v3.9 仅在真实失效和维修数据满足条件时实施
```

## 10. 统一安全原则

1. 默认关闭；
2. 输入、容量、时间窗口、队列、查询和保留期有界；
3. 结果可解释、可版本化、可审计、可回退；
4. SQL、MES 或模型故障不阻塞控制链路；
5. 人工操作保存身份、原因和时间；
6. 不在仓库保存现场密钥、真实点位、维修规则和未脱敏数据；
7. 不直接写 PLC、不自动停机、不修改调度；
8. 自动控制联动必须另立安全项目、风险分析和现场验收。
