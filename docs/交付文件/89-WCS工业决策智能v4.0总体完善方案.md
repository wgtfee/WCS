# WCS Industrial Decision Intelligence v4.0 总体完善方案

## 1. 文档定位

本文档定义 WCS Runtime Engine 在统一调度、异常诊断、深度学习、故障概率、RUL、预测性维护和 Simulation & Verification S0～S10 完成后的下一条独立演进主线：

```text
WCS Industrial Decision Intelligence v4.0
```

该主线不再继续命名为 Simulation S11，也不建立 EMS/RGV“第十三阶段”。其目标不是继续堆叠模型，而是把现有数据、模型、预测、维修、仿真和调度能力组织成一套可版本化、可解释、可回放、可审批、可回滚和持续评估的工业决策闭环。

本文档是总体架构和治理基线；具体开发顺序、任务拆分、CI、迁移和验收见《90-WCS工业决策智能v4.0分阶段执行手册》。

## 2. 当前软件基线

当前 `develop` 已具备以下能力：

- EMS/RGV 统一调度、路权、交通冲突、死锁检测和恢复；
- PLC Telemetry、WAL、规则异常、统计异常、Isolation Forest、本地 ONNX；
- 多模型 Evidence Fusion、资产健康评分、趋势和 SQL 历史；
- 根因图、传播路径、人工复核、维修建议、MES Outbox 和维修反馈；
- 故障概率、RUL 区间、Outcome Journal 和回测指标；
- Simulation & Verification S0～S10：场景治理、虚拟 PLC/RGV/交通、外部故障、Synthetic Health/RUL、集成恢复、容量长稳、HIL 软件治理和统一验证中心；
- S10 软件全回归 46/46 exact-head 成功。

当前主要缺口不是“是否有 AI”，而是：

1. 模型、特征、数据集、阈值、部署状态和回滚缺少统一生命周期；
2. 在线推理和离线训练特征缺少统一定义、时间点一致性和血缘；
3. 预测结果尚未形成标准化、受约束、可评估的决策建议；
4. 维修结果、调度结果和真实故障结果尚未形成持续学习闭环；
5. 仿真已能验证功能，但尚未成为调度策略和多目标优化实验平台；
6. Trace、Evidence、ModelVersion、FeatureSnapshot 和业务 Outcome 尚未全链路关联；
7. AI 能力等级、人工审批、自动化边界和失效回退需要统一治理。

## 3. 建设目标

### 3.1 总体目标

形成以下闭环：

```text
PLC / Task / Dispatch / Alarm / Maintenance / Outcome
                         ↓
                 Governed Feature Center
                         ↓
         Rule / Statistical / ML / ONNX / RUL
                         ↓
             Governed ModelOps Runtime
                         ↓
              Decision Proposal（建议）
                         ↓
       Safety Constraint + Approval + Policy
                         ↓
     Shadow / Manual / Bounded Automatic Execution
                         ↓
        Actual Outcome + Maintenance Feedback
                         ↓
         Evaluation / Drift / Rollback / Retraining
```

### 3.2 可衡量目标

v4.0 软件完成后至少应达到：

- 所有正式模型均有唯一版本、ManifestHash、ArtifactSha256、FeatureSchemaHash 和训练数据版本；
- 模型发布支持 Candidate、Shadow、Champion、Fallback、Retired 状态；
- 在线推理可以确定性重建当时的 FeatureSnapshot；
- 每条 DecisionProposal 可追溯到模型、特征、规则、约束和证据；
- AI 建议默认不进入 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager 或交通控制写路径；
- Shadow 建议与真实结果可自动关联并计算有效率、误报、漏报、成本和产能影响；
- 新模型和新策略必须先通过离线回放、仿真、Shadow 和人工批准；
- 任何模型、特征、SQL、MES 或优化器故障不得阻塞确定性控制链路；
- 模型和策略可在分钟级完成逻辑回退，不要求重启生产控制服务；
- 生产环境默认关闭未经批准的 AI 自动化等级。

## 4. 范围与非范围

### 4.1 本项目范围

- ModelOps 全生命周期；
- Feature Center；
- Decision Support / Shadow Decision；
- Maintenance Outcome 闭环；
- 因果诊断和反事实分析的受控基础；
- Digital Twin Strategy Optimizer；
- 多目标调度建议；
- 模型、特征、决策和 Outcome 的统一 Evidence；
- AI 治理、权限、审批、审计和回滚；
- 与 MES 的维修工单、批准和结果回传接口；
- Desktop/Web 只读或审批型管理页面。

### 4.2 明确不在本阶段自动执行的能力

- 急停、复位、安全门、光栅、机械联锁；
- 绕过 PLC 安全逻辑；
- 强制写 PLC 点位；
- 自动解除闭塞、路权或死锁；
- 未经规则引擎和审批的自动派单、停机或取消任务；
- 自动修改安全参数、硬件拓扑或现场工艺参数；
- 使用大语言模型直接产生可执行 PLC/调度命令。

## 5. 统一设计原则

1. **确定性控制优先**：`Wcs.Core` 的状态机、资源锁、路权、交通、PLC 联锁和恢复逻辑保持唯一控制事实来源。
2. **AI 默认只建议**：AI 输出 `DecisionProposal`，不直接输出设备命令。
3. **Fail-closed**：模型、特征、审批、证据或环境条件不完整时，返回 Disabled/Unavailable/InsufficientEvidence，不生成伪决策。
4. **模块化单体优先**：第一阶段继续在现有 .NET 8 解决方案内按模块隔离，不因“AI”强制重构为微服务。
5. **可外置但不强依赖**：训练、批量回放、模型仓库后续可独立服务化；生产控制运行时不依赖外部云服务。
6. **本地离线可运行**：模型、特征、策略和证据支持无互联网现场。
7. **版本不可变**：相同 Version 不得对应不同 Hash、FeatureSchema、数据集或审批信息。
8. **证据优先于结论**：任何模型激活、策略升级和自动化等级提升必须有 exact-head Evidence。
9. **有界资源**：队列、窗口、模型大小、批次、查询、缓存、保留期和并发必须配置上限。
10. **可观测、可解释、可回滚**：所有建议和切版都必须保留原因、输入、版本、Actor、时间和回退路径。

## 6. 总体逻辑架构

```text
┌──────────────────────────────────────────────────────────────┐
│ Wcs.Host / Wcs.Desktop / MES API                             │
│ 查询、审批、发布、回滚、Evidence、策略实验、Outcome 回传       │
└──────────────────────────────┬───────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────┐
│ Wcs.DecisionIntelligence                                     │
│ Proposal / Explanation / Constraint / Approval / Outcome     │
└──────────────┬───────────────────────────────┬───────────────┘
               │                               │
┌──────────────▼──────────────┐   ┌────────────▼───────────────┐
│ Wcs.ModelOps                │   │ Wcs.Optimization            │
│ Registry/Deploy/Shadow/     │   │ Policy Candidate/Digital    │
│ Champion/Drift/Rollback     │   │ Twin/Multi-objective Score  │
└──────────────┬──────────────┘   └────────────┬───────────────┘
               │                               │
┌──────────────▼───────────────────────────────▼───────────────┐
│ Wcs.FeatureCenter                                             │
│ Definition/Snapshot/Online/Historical/PIT Join/Quality/Lineage│
└──────────────┬───────────────────────────────────────────────┘
               │
┌──────────────▼───────────────────────────────────────────────┐
│ Existing WCS Runtime                                          │
│ StateCenter/EventBus/Alarm/Task/Dispatch/Traffic/Telemetry/   │
│ Health/RootCause/Maintenance/Forecast/Simulation S0～S10      │
└──────────────────────────────────────────────────────────────┘
```

## 7. 建议工程结构

第一阶段建议在现有解决方案内新增项目或明确模块：

```text
src/
├── Wcs.ModelOps/
│   ├── Registry/
│   ├── Packages/
│   ├── Deployment/
│   ├── Shadow/
│   ├── Evaluation/
│   ├── Drift/
│   ├── Rollback/
│   └── Audit/
│
├── Wcs.FeatureCenter/
│   ├── Definitions/
│   ├── Materialization/
│   ├── Snapshots/
│   ├── OnlineStore/
│   ├── HistoricalStore/
│   ├── Quality/
│   ├── Lineage/
│   └── DatasetBuilder/
│
├── Wcs.DecisionIntelligence/
│   ├── Proposals/
│   ├── Explanations/
│   ├── Constraints/
│   ├── Approval/
│   ├── Policies/
│   ├── Outcomes/
│   └── Audit/
│
├── Wcs.Optimization/
│   ├── Objectives/
│   ├── PolicyCandidates/
│   ├── Experiments/
│   ├── DigitalTwin/
│   └── Ranking/
│
└── Wcs.IndustrialIntelligence.Tests/
```

如果暂时不增加项目，也必须在 `Wcs.Core` 之外保持独立命名空间，禁止把 AI 发布、训练、策略实验逻辑写进 PLC/调度核心对象。

## 8. ModelOps 设计

### 8.1 核心组件

```text
ModelRegistry
ModelPackageValidator
ModelDeploymentPolicy
ShadowInferenceEngine
ChampionChallengerEvaluator
ModelDriftMonitor
ModelRollbackManager
ModelAuditCenter
```

### 8.2 模型状态机

```text
Draft
  → Validated
  → Candidate
  → Shadow
  → Champion
  → Fallback
  → Retired

任意活动状态
  → Quarantined
```

状态转换要求：

- `Draft → Validated`：Manifest、Artifact SHA、FeatureSchema、输入输出 Shape 和资源上限全部通过；
- `Validated → Candidate`：离线回放和最小质量门槛通过；
- `Candidate → Shadow`：人工批准，且不得写控制链路；
- `Shadow → Champion`：样本量、稳定性、准确率、延迟、内存和业务 Outcome 门槛通过；
- `Champion → Fallback`：新 Champion 激活时自动保留上一个可用版本；
- 任意版本校验失败、漂移超限或运行异常时进入 `Quarantined`；
- 回滚只切换逻辑指针，不覆盖历史版本和 Evidence。

### 8.3 ModelPackage 最小内容

```text
model.onnx
manifest.json
feature-schema.json
normalization.json
validation-evidence.json
license.txt（如适用）
```

Manifest 至少包括：

```text
ModelId
ModelVersion
ModelType
ArtifactFile
ArtifactSha256
ManifestHash
FeatureSchemaId
FeatureSchemaHash
TrainingDatasetVersion
TrainingDatasetHash
TrainingAssetCount
FailureEventCount
ValidationMetrics
RuntimeLimits
ApprovedBy
ApprovedAtUtc
FallbackVersion
```

### 8.4 Champion/Challenger

同一 AssetType/Profile 同时最多允许：

- 1 个 Champion；
- 1 个 Fallback；
- 配置数量上限内的 Candidate/Shadow。

Shadow 推理必须：

- 使用与 Champion 同一 FeatureSnapshot；
- 不写控制路径；
- 分离超时和资源预算；
- 保存结果差异、延迟、错误和 Outcome；
- Candidate 失败不能影响 Champion。

## 9. Feature Center 设计

### 9.1 核心组件

```text
FeatureDefinitionRegistry
RealtimeFeatureMaterializer
HistoricalFeatureMaterializer
FeatureSnapshotService
FeatureQualityValidator
FeatureLineageTracker
PointInTimeDatasetBuilder
FeatureRetentionManager
```

### 9.2 FeatureDefinition

每个特征必须定义：

```text
FeatureId
Name
EntityType
DataType
Unit
Source
Aggregation
Window
Freshness
DefaultPolicy
NullPolicy
ValidRange
Version
DefinitionHash
Owner
```

禁止模型代码自行隐藏计算特征。模型使用的特征顺序、单位、窗口和缺失策略必须来自已审批的 FeatureSchema。

### 9.3 FeatureSnapshot

每次正式推理保存或可确定性重建：

```text
SnapshotId
EntityId
AsOfUtc
FeatureSchemaId
FeatureSchemaHash
ValuesHash
SourceOffsets
QualityStatus
MaterializerVersion
```

### 9.4 在线与历史存储

初期建议：

```text
实时特征：进程内有界缓存；跨实例时可选 Redis
历史特征：SQL Server + Parquet 冷存
模型与 Evidence：本地受控文件仓库
元数据：SQL Server
```

Feature Center 不应要求生产站点必须部署 Redis、Kafka 或云 Feature Store。外部组件是可选扩展，不是控制运行时硬依赖。

### 9.5 Point-in-Time 正确性

训练集构建必须以 Outcome 发生前的 `AsOfUtc` 获取特征，禁止使用未来数据。任何 Dataset 必须记录：

```text
DatasetVersion
DatasetHash
QueryDefinitionHash
FeatureSchemaHash
TimeRange
AssetScope
LabelDefinitionVersion
GeneratedAtUtc
GeneratedBy
```

## 10. Decision Intelligence 设计

### 10.1 标准输出

```csharp
public sealed class DecisionProposal
{
    public string ProposalId { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string ProposalType { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string Explanation { get; init; } = string.Empty;
    public double FailureRisk { get; init; }
    public double ThroughputImpact { get; init; }
    public double EnergyImpact { get; init; }
    public double MaintenanceCostImpact { get; init; }
    public string ModelVersion { get; init; } = string.Empty;
    public string FeatureSnapshotId { get; init; } = string.Empty;
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public IReadOnlyList<string> ConstraintViolations { get; init; } = [];
    public bool RequiresApproval { get; init; }
}
```

### 10.2 Proposal 生命周期

```text
Generated
  → ShadowRecorded
  → PendingApproval
  → Approved / Rejected / Expired
  → ExecutedExternally
  → OutcomeObserved
  → Evaluated
```

`ExecutedExternally` 表示由既有确定性服务、MES 或人工执行并回写结果，不表示 Decision Intelligence 直接写 PLC。

### 10.3 约束顺序

```text
Safety Constraints（硬约束）
  ↓
Control State / Interlock（硬约束）
  ↓
Route / Reservation / Traffic（硬约束）
  ↓
Business SLA / Priority（强约束）
  ↓
Health / Failure Risk / Maintenance（优化目标）
  ↓
Energy / Wear / Distance（优化目标）
```

AI 分数不得覆盖硬约束。任何硬约束失败，Proposal 必须返回 `Blocked` 并记录具体约束。

## 11. AI 自动化等级

| 等级 | 名称 | 能力 | 默认策略 |
|---|---|---|---|
| L0 | ReadOnly | 查询、解释、趋势、Evidence | 可在受控环境开放 |
| L1 | Advisory | 生成建议，不产生执行请求 | v4.0 默认目标 |
| L2 | ApprovalRequired | 人工批准后由确定性服务执行低风险动作 | 后续现场验收 |
| L3 | BoundedAutomation | 受规则、额度、时间窗和回滚保护的自动优化 | 独立安全立项 |
| L4 | SafetyCritical | 安全联锁、急停、强制控制 | 永久禁止 AI 直接执行 |

任何环境和 ProposalType 必须显式配置最大允许等级；缺失配置时按 L0 处理。

## 12. Maintenance Outcome 与因果闭环

维修闭环应统一关联：

```text
HealthEventId
RootCauseAnalysisId
MaintenanceProposalId
MesWorkOrderId
MaintenanceActionId
FeatureSnapshotBefore
FeatureSnapshotAfter
ForecastBefore
ForecastAfter
ObservedFailure
Downtime
Cost
Effectiveness
```

Outcome 分类至少包括：

```text
ObservedFailure
PreventiveMaintenanceEffective
PreventiveMaintenanceIneffective
CorrectiveMaintenanceEffective
NoActionNoFailure
Censored
InvalidEvidence
```

第一阶段只做基于规则和图的因果候选，不宣称统计因果成立。反事实分析必须标记为 `Estimated`，不得替代工程人员结论。

## 13. Digital Twin Optimization

### 13.1 目标

把 S0～S10 从验证平台升级为策略实验平台，在同一订单、Seed、拓扑、设备状态和故障条件下比较不同调度策略。

### 13.2 多目标指标

```text
Throughput
MissionLeadTime
P95WaitTime
DeadlockCount
ConflictCount
EnergyEstimate
WearIndex
FailureRiskExposure
MaintenanceWindowViolation
SlaViolation
RecoveryTime
```

### 13.3 策略生命周期

```text
Draft Policy
  → Static Validation
  → Deterministic Replay
  → Scenario Matrix
  → Stress/Soak
  → Shadow Recommendation
  → Approved Candidate
```

Digital Twin Optimizer 只能输出 PolicyCandidate 和 Evidence，不得直接替换生产策略。正式切换仍由 DeploymentPolicy、审批和运行时边界控制。

## 14. 数据库建议

建议新增以下逻辑表；实际命名需遵循项目 SqlSugar 规范：

```text
Wcs_AiModelRegistry
Wcs_AiModelPackage
Wcs_AiModelDeployment
Wcs_AiModelEvaluation
Wcs_AiModelDriftEvent
Wcs_AiModelAuditJournal

Wcs_FeatureDefinition
Wcs_FeatureSchema
Wcs_FeatureSnapshot
Wcs_FeatureDataset
Wcs_FeatureQualityEvent
Wcs_FeatureLineage

Wcs_DecisionProposal
Wcs_DecisionConstraintResult
Wcs_DecisionApprovalJournal
Wcs_DecisionOutcomeJournal

Wcs_OptimizationPolicy
Wcs_OptimizationExperiment
Wcs_OptimizationRun
Wcs_OptimizationMetric
```

共同规则：

- Journal 和 Evidence 表只追加；
- Hash、Version、Actor、Reason、CreatedAtUtc 必填；
- 大型特征向量和模型文件不直接存 SQL，可存受控文件并保存 SHA/Path；
- 所有查询必须有资产、时间和数量上限；
- 删除使用 Retention/Archive，不覆盖已审批 Evidence。

## 15. Host API 建议

统一使用只读查询和治理型写操作，禁止控制型 API：

```text
GET  /api/industrial-intelligence/models
GET  /api/industrial-intelligence/models/{modelId}/versions
POST /api/industrial-intelligence/models/{version}/validate
POST /api/industrial-intelligence/models/{version}/approve-shadow
POST /api/industrial-intelligence/models/{version}/promote
POST /api/industrial-intelligence/models/{version}/rollback

GET  /api/industrial-intelligence/features/definitions
GET  /api/industrial-intelligence/features/snapshots/{snapshotId}
POST /api/industrial-intelligence/datasets/build

GET  /api/industrial-intelligence/proposals
GET  /api/industrial-intelligence/proposals/{proposalId}
POST /api/industrial-intelligence/proposals/{proposalId}/approve
POST /api/industrial-intelligence/proposals/{proposalId}/reject
POST /api/industrial-intelligence/proposals/{proposalId}/outcome

POST /api/industrial-intelligence/experiments
GET  /api/industrial-intelligence/experiments/{id}
```

所有写操作要求：

- 身份认证和细粒度权限；
- Actor、Reason、CorrelationId；
- 幂等键；
- 乐观并发；
- 状态机校验；
- 审计 Journal；
- Production 环境白名单。

## 16. 与现有模块的集成边界

### 16.1 允许读取

- `StateCenter` 资产和设备状态；
- `MetricsCenter` 指标；
- Telemetry 和 Health History；
- Alarm、RootCause、Maintenance、Forecast；
- Task、Dispatch、Route、Traffic 的只读快照；
- Simulation Run 和 Evidence。

### 16.2 禁止直接调用

v4.0 L0/L1 阶段禁止直接依赖或调用：

```text
IPlcConnection / PLC Write
CommandBus.Send control command
TaskOrchestrator mutation
DeviceManager control
UnifiedTransportDispatchEngine mutation
TransportTrafficCoordinator mutation
RouteReservation mutation
Emergency / Safety Interlock mutation
```

需要执行的建议必须转换成受治理的外部请求，由现有确定性应用服务重新校验全部约束后执行。

## 17. 可观测性与 Evidence

统一关联字段：

```text
TraceId
CorrelationId
CausationId
EvidenceId
EntityId
TaskId
DispatchDecisionId
ModelId
ModelVersion
FeatureSnapshotId
ProposalId
OutcomeId
ExperimentId
```

关键指标：

- 推理请求、成功、失败、超时、隔离；
- P50/P95/P99 推理延迟；
- 模型内存、加载时间、并发和队列；
- 特征新鲜度、缺失率、越界率和漂移；
- Proposal 生成、阻止、批准、拒绝、过期；
- Shadow 与真实结果一致率；
- Champion/Challenger 业务指标差异；
- 回滚次数和原因；
- 优化实验吞吐、等待、风险、能耗和稳定性。

Evidence Artifact 至少保存：

```text
exact Head SHA
configuration hash
model manifest hash
feature schema hash
dataset hash
scenario/policy hash
test result
resource result
artifact digest
```

## 18. 安全与权限

建议权限：

```text
IndustrialIntelligence.View
FeatureDefinition.Manage
Dataset.Build
Model.Upload
Model.Validate
Model.ApproveShadow
Model.Promote
Model.Rollback
Decision.View
Decision.Approve
Decision.RecordOutcome
Optimization.Run
Optimization.ApprovePolicy
```

高风险操作必须双人审批：

- Champion 切换；
- Fallback 失效或删除；
- L2/L3 自动化等级提升；
- FeatureSchema 破坏性变更；
- 生产策略切换；
- 阈值大幅放宽。

仓库不得保存现场密钥、真实模型、未脱敏数据集、生产连接串和第三方许可证文件。

## 19. 部署策略

### 19.1 第一阶段

保持模块化单体：

```text
Wcs.Host
  + ModelOps application services
  + Feature materialization background services
  + Decision shadow services

SQL Server
Local model/evidence store
Optional Redis
```

优点是权限、部署、事务、运维和离线现场更简单，不要求现有系统立即微服务化。

### 19.2 可选服务化条件

只有出现以下情况才拆分服务：

- 批量训练/回放占用明显影响 Host；
- 多个系统共享同一模型仓库或 Feature Center；
- 推理需要独立 GPU/高并发资源；
- 不同团队和发布周期需要隔离；
- 安全域要求物理隔离。

即使服务化，WCS 确定性控制不得同步依赖 AI 服务可用性。

## 20. 非功能要求

- Production 默认关闭 Candidate、Shadow 之外的未经批准能力；
- AI 后台服务崩溃不影响 PLC Polling、TaskScheduler 和调度；
- 所有 Channel/Queue 有界并有 Drop/Backpressure 策略；
- 单个模型包大小、加载内存、并发、超时可配置且有硬上限；
- SQL 写失败通过独立 Outbox/Retry 处理，不阻塞控制线程；
- 时间统一使用 UTC，展示层转换本地时区；
- Hash 使用 SHA-256；
- 状态机和 Journal 重启可恢复；
- 所有公开 API 支持分页、过滤和最大时间范围；
- 所有算法结果可在固定输入和版本下重放。

## 21. 正式阶段路线

本主线使用 `IDI-P0～IDI-P6`，不使用 S11：

| 阶段 | 名称 | 核心交付 |
|---|---|---|
| IDI-P0 | 治理与契约基线 | 模块边界、状态机、权限、Evidence、配置和 CI 骨架 |
| IDI-P1 | ModelOps Center | Registry、Package、Candidate/Shadow/Champion/Fallback、回滚 |
| IDI-P2 | Feature Center | Definition、Schema、Snapshot、PIT Dataset、质量和血缘 |
| IDI-P3 | Shadow Decision | Proposal、Constraint、Explanation、Approval、Outcome |
| IDI-P4 | Maintenance Learning | 维修结果闭环、效果评估、根因候选和标签治理 |
| IDI-P5 | Digital Twin Optimizer | 策略实验、多目标比较、Replay、Stress/Soak |
| IDI-P6 | Bounded Automation Readiness | L2/L3 软件治理、额度、熔断、回滚和现场验收准备 |

IDI-P6 只交付软件准备度；真正自动控制必须独立完成 HAZOP/FMEA、机械安全、现场试运行和 Site Acceptance。

## 22. 总体完成定义

v4.0 软件主线完成要求：

- IDI-P0～P5 的功能、测试、文档和 exact-head 回归完成；
- Model/Feature/Decision/Outcome/Experiment 均可版本化和审计；
- Shadow 运行不产生控制写入；
- Champion/Challenger 和回滚可验证；
- Feature Point-in-Time 和数据血缘可验证；
- Proposal 被硬约束正确阻止；
- Maintenance Outcome 能闭环并形成受治理标签；
- Digital Twin 实验可确定性重放；
- 资源、异常、重启、SQL 故障和坏模型隔离通过；
- Production fail-closed；
- S0～S10 现有能力无回归。

自动控制完成不属于上述软件完成定义。

## 23. 最终结论

WCS 下一步最有价值的升级不是增加更多孤立模型，而是建立：

```text
ModelOps Center
+ Feature Center
+ Decision Support Shadow Mode
+ Maintenance Outcome Learning
+ Digital Twin Optimization
```

其核心边界保持不变：

> AI 负责预测、解释、建议和策略候选；WCS 确定性核心负责安全、约束和执行。任何自动化等级提升必须经过独立安全立项和真实现场验收。
