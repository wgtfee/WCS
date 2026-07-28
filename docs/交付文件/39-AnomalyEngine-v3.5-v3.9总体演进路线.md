# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断演进路线。

```text
EMS/RGV 统一调度主线：第一阶段～第十二阶段，软件研发已经结束
AnomalyEngine 诊断扩展线：v1～v3.9，继续以只读诊断和人工辅助决策演进
```

统一调度后续不再使用“第十三阶段”扩展。现场工作使用点位包、拓扑包、参数包、规则包、缺陷单、验收报告和发布单推进。AnomalyEngine 不得绕过既有状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

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
| v3.7 | 维修建议、反馈闭环、MES 工单字段、指标和标签候选 | 功能与专项 SQL E2E 已完成，等待最新完整矩阵和合并 |

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

已交付：

- 按等级和连续评估次数创建/恢复事件；
- 同一资产一个活动事件；
- Raised、Observed、GradeChanged、Acknowledged、Suppressed、Unsuppressed、Recovered；
- SQL Journal 双重幂等；
- MES HTTP Outbox、指数退避、DeadLetter 和人工重试；
- 2xx/409 幂等成功；
- Host 重启恢复；
- SQL、MES 和网络故障不阻塞 PLC、任务和调度。

边界：不停止设备、不取消任务、不修改车辆、路径或路权、不替代 AlarmCenter、不把 MES 响应当作 PLC 联锁。

## 5. v3.6：根因关联与异常传播分析

### 5.1 已交付能力

- 版本化、带来源和审批信息的依赖图；
- Asset、Component、Signal、Task、Station、Segment；
- DependsOn、Feeds、Controls、LocatedAt、Carries；
- GraphHash 和同版本不同 Hash 拒绝；
- 节点、边、引用、权重、自环、容量和环路校验；
- 生产默认 `AllowCycles=false`；
- 有界时间窗口、上游搜索和最短传播路径；
- RootCause、Intermediate、Symptom；
- Coverage、Topology、Temporal、Severity 排序；
- 确定性 AnalysisId 和 SQL 幂等；
- 图版本、不可变分析快照和人工复核 Journal；
- Confirmed、Rejected、Supplemented；
- Host 重启恢复。

### 5.2 验收

PR #29 已通过最终矩阵并 Squash 合入 `develop`：

```text
Merge SHA: 900353ef8f17b3ab38cb4e711529c3fcc3629892
Specialty: WCS Asset Health Root Cause #9
Artifact: wcs-asset-health-root-cause-9
Digest: sha256:44688aa44d8710b24c1372b9dcf0dccc53f9e7165e94353d7ed034f8860194eb
```

边界：Confidence 不是故障概率；无图映射不猜测；Supplemented 不修改活动图；图修改必须新版本审批；不自动停机、取消任务或修改调度。

## 6. v3.7：维修决策支持与反馈闭环

### 6.1 目标

把 v3.5 活动健康事件和 v3.6 已人工复核根因转换为可执行检查建议，并利用维修反馈评估规则采纳、故障命中、误报和维修效果。

### 6.2 已实现能力

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

### 6.3 SQL 对象

```text
Wcs_AssetHealthMaintenanceRuleSetVersion
Wcs_AssetHealthMaintenanceRecommendation
Wcs_AssetHealthMaintenanceFeedbackJournal
Wcs_AssetHealthMaintenanceTrainingLabel
```

反馈只更新建议生命周期摘要并追加 Journal，不覆盖原始 RecommendationJson、v3.5 事件或 v3.6 分析。

### 6.4 专项验收

```text
Workflow: WCS Asset Health Maintenance #3
Run ID: 30344513405
Source SHA: 620eb179bfdb6d349487ebe931784f8c220e1349
Artifact: wcs-asset-health-maintenance-3
Digest: sha256:b572875eadfbeafdb8e413ef8ed6ac7f42400ede4b189c0c6ae85987c479675f
Conclusion: success
```

断言：RuleSet=1、Recommendation=1、Feedback=2、TrainingLabel=1；重复建议和反馈幂等；Accepted→Repaired；MES 工单字段；PostHealthScore=92；fault-confirmed 标签人工 Approved；Host 重启恢复。

### 6.5 完成门槛

- Core 规则和安全边界测试通过；
- SQL 四表、索引和精确计数通过；
- 建议/反馈/标签幂等通过；
- MES 工单字段和指标通过；
- Host 重启恢复通过；
- Production 默认关闭、规则集为空；
- 最新源码与文档 Head 完整回归和一小时 Soak 全绿；
- 文档 21、39、46～48 完整；
- PR #30 Ready 并合入 `develop`。

### 6.6 明确边界

- 建议是检查建议，不是控制命令；
- MES 工单号只是审计字段；
- 无规则不生成建议；
- 训练标签 Approved 不自动训练、激活或替换模型；
- 不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单；
- 真实维修 SOP、权限和正式投产属于项目级验收。

## 7. v3.8：可插拔模型与 ONNX 运行时

### 7.1 目标

保留纯 .NET Isolation Forest，同时建立统一模型适配边界，使本地 ONNX 模型复用 Profile、治理、Shadow、Canary、回滚和 Evidence Fusion。

### 7.2 计划能力

- `IAnomalyModelAdapter`；
- 统一特征、输入维度、标准化和输出解释；
- Isolation Forest Adapter 和本地 ONNX Runtime Adapter；
- 模型摘要、输入输出名称和维度校验；
- 冻结数据集、审批、Shadow、Canary 和原子激活；
- 模型输出只生成 Evidence，不写 PLC；
- 推理超时、异常和内存有界；
- 无 GPU、无外网环境可运行。

启动条件：存在经过现场验收的本地模型、稳定特征和明确许可证；需通过重复性、十万窗口吞吐、回滚、内存和失败降级测试。

## 8. v3.9：故障概率与剩余寿命预测

### 8.1 目标

在具备长期退化数据、真实故障和维修记录后，输出部件未来故障概率、剩余寿命区间和建议维护窗口。

### 8.2 计划能力

- 部件级退化轨迹；
- 指定时间窗内故障概率；
- RUL 点估计和置信区间；
- 按产品、负载、工况和维修状态分层；
- 时间切分、数据泄漏检查和离线回测；
- 校准、覆盖率、提前量和误报评估；
- 维修后退化状态重置或迁移；
- 输出建议维护窗口，不自动下发停机。

数据门槛：足够长连续历史、明确失效时间、部件更换与维修记录、工况上下文、可信标签和足够真实失效样本。数据不足时保持关闭，不输出虚假 RUL。

## 9. 执行顺序

```text
v3.5 已完成并合入 develop
  ↓
v3.6 已完成并合入 develop
  ↓
v3.7 功能与专项已完成，等待最终矩阵和合并
  ↓
v3.8 仅在成熟本地模型存在时实施
  ↓
v3.9 仅在真实失效和维修数据满足条件时实施
```

## 10. 统一安全原则

所有阶段必须满足：

1. 默认关闭；
2. 输入、容量、时间窗口、队列、查询和保留期有界；
3. 结果可解释、可版本化、可审计、可回退；
4. SQL、MES 或模型故障不阻塞控制链路；
5. 人工操作保存身份、原因和时间；
6. 不在仓库保存现场密钥、真实点位、维修规则和未脱敏数据；
7. 不直接写 PLC、不自动停机、不修改调度；
8. 自动控制联动必须另立安全项目、风险分析和现场验收。
