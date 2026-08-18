# WCS Industrial Decision Intelligence v4.0 分阶段执行手册

## 1. 文档目的

本文档把《89-WCS工业决策智能v4.0总体完善方案》转换成可直接执行的研发计划，定义分支、目录、数据库、API、测试、CI、Evidence、迁移、回滚和阶段完成条件。

本执行手册适用于：

- `wgtfee/WCS` 仓库；
- 基线分支 `develop`；
- .NET 8、SqlSugar、SQL Server、Avalonia、现有 Host/Application/Core/Infrastructure 分层；
- 离线工业现场；
- AI 默认 L0/L1，只读和建议模式。

本手册不允许把本路线命名为 Simulation S11，也不允许未经现场安全验收把 AI 建议接入 PLC 或调度控制写路径。

## 2. 执行总原则

1. 每个阶段从最新 `develop` 创建独立功能分支；
2. 每个阶段必须先完成契约和安全边界，再实现功能；
3. 每个阶段至少包含设计文档、专项测试报告、操作手册；
4. 每个阶段必须有固定 child 数量的 exact-head Full Regression；
5. 不允许通过降低阈值、删除测试、扩大默认权限或伪造数据完成门禁；
6. AI、SQL、模型仓库和 Feature Center 故障不得阻塞控制链路；
7. 所有模型、特征、策略、数据集和 Evidence 版本不可变；
8. 所有治理写操作记录 Actor、Reason、Utc、CorrelationId 和幂等键；
9. Production 默认关闭，必须显式批准；
10. 阶段完成只代表软件完成，不代表现场安全或自动控制验收。

## 3. 分支与发布策略

### 3.1 分支命名

```text
feature/idi-p0-governance-contracts
feature/idi-p1-modelops-center
feature/idi-p2-feature-center
feature/idi-p3-shadow-decision
feature/idi-p4-maintenance-learning
feature/idi-p5-digital-twin-optimizer
feature/idi-p6-bounded-automation-readiness
```

### 3.2 PR 规则

- Base 固定为 `develop`；
- 开发期间保持 Draft；
- Functional Head 冻结后禁止混入无关修改；
- 任何 Head 移动都必须重新运行当前阶段专项和累计回归；
- Ready 前核实 Artifact ID、Artifact 名称、未过期状态和 SHA-256 Digest；
- Squash Merge；
- 合并说明必须区分软件完成和现场完成。

### 3.3 提交建议

```text
feat(idi-p1): add model registry contracts
feat(idi-p1): add model package validation
feat(idi-p1): add shadow deployment runtime
feat(idi-p1): add modelops host api
feat(idi-p1): add modelops tests and workflow
docs(idi-p1): add design test and operation documents
```

避免一个提交同时修改模型治理、PLC 驱动、调度算法和 UI。

## 4. 目标目录

```text
src/
├── Wcs.ModelOps/
├── Wcs.FeatureCenter/
├── Wcs.DecisionIntelligence/
├── Wcs.Optimization/
├── Wcs.IndustrialIntelligence.Tests/
├── Wcs.Application/
├── Wcs.Infrastructure/
├── Wcs.Host/
└── Wcs.Desktop/

docs/交付文件/
├── 89-WCS工业决策智能v4.0总体完善方案.md
├── 90-WCS工业决策智能v4.0分阶段执行手册.md
└── 后续阶段文档

.github/workflows/
├── idi-p0-governance.yml
├── idi-p0-full-regression.yml
├── idi-p1-modelops.yml
├── idi-p1-full-regression.yml
└── ...
```

如暂时不新增 csproj，可先在独立目录和命名空间实现，但 P1 结束前应完成项目级隔离评估。

## 5. 全局配置基线

建议新增：

```json
{
  "IndustrialIntelligence": {
    "Enabled": false,
    "Mode": "ReadOnly",
    "AllowedEnvironments": ["Development", "IndustrialIntelligence", "IndustrialIntelligenceLoadTest"],
    "MaximumAutomationLevel": "L1",
    "MaximumPendingProposals": 10000,
    "ProposalRetentionDays": 180,
    "EvidenceRetentionDays": 365,
    "DefaultInferenceTimeoutMs": 200,
    "MaximumModelPackageBytes": 268435456,
    "MaximumLoadedModels": 8,
    "MaximumConcurrentInference": 4,
    "FeatureSnapshotRetentionDays": 90,
    "MaximumDatasetRows": 5000000
  }
}
```

Production 配置：

```json
{
  "IndustrialIntelligence": {
    "Enabled": false,
    "Mode": "ReadOnly",
    "AllowedEnvironments": [],
    "MaximumAutomationLevel": "L0"
  }
}
```

禁止在通用配置中保存真实模型路径、生产密钥、MES URL、SQL 密码或现场资产点位。

## 6. IDI-P0：治理与契约基线

### 6.1 目标

在实现 ModelOps 和 Feature Center 前，先固定所有跨模块契约、安全等级、Evidence、状态机和环境边界，防止后续模块直接侵入控制核心。

### 6.2 开发任务

#### 核心契约

```text
IndustrialIntelligenceOptions
AutomationLevel
IndustrialIntelligenceEnvironmentGuard
EvidenceReference
VersionedHashReference
ActorReason
BoundedQuery
```

#### 状态定义

```text
ModelLifecycleStatus
FeatureSchemaStatus
DecisionProposalStatus
OptimizationPolicyStatus
```

#### 安全边界扫描

静态禁止依赖：

```text
IPlcConnection
S7Client
Snap7
CommandBus control methods
TaskOrchestrator mutation
DeviceManager control
UnifiedTransportDispatchEngine mutation
TransportTrafficCoordinator mutation
IRouteReservationManager mutation
```

#### Host

```text
GET /api/industrial-intelligence/status
GET /api/industrial-intelligence/capabilities
```

仅返回环境、Enabled、Mode、MaximumAutomationLevel 和可用模块，不提供执行入口。

### 6.3 数据库

P0 只建立最小审计表：

```text
Wcs_IndustrialIntelligenceAuditJournal
Wcs_IndustrialIntelligenceEvidence
```

### 6.4 测试

至少验证：

- Production fail-closed；
- 未批准环境返回 404 或 Disabled；
- 默认 L0；
- 配置数组无重复绑定问题；
- Hash 格式和不可变规则；
- Actor/Reason 必填；
- API 只读；
- Core 项目无 PLC/调度写依赖；
- 有界配置拒绝非法值；
- 审计 Journal 只追加。

### 6.5 CI

```text
WCS IDI P0 Governance Contract
WCS IDI P0 Full Regression
```

P0 Full Regression = S10 46-child 软件矩阵 + P0 专项，固定为 47-child。

### 6.6 完成条件

- 契约、配置、状态机、权限和 Evidence 固定；
- 专项测试全绿；
- 47/47 exact-head；
- Production fail-closed；
- 控制写入为 0；
- 文档和 Artifact 固化。

## 7. IDI-P1：ModelOps Center

### 7.1 目标

建立模型 Registry、Package 验证、Candidate/Shadow/Champion/Fallback、隔离、回滚和审计，统一接管现有 ONNX/ML 模型版本治理。

### 7.2 第一批领域对象

```text
AiModelDefinition
AiModelVersion
AiModelPackageManifest
AiModelDeployment
AiModelEvaluation
AiModelDriftEvent
AiModelAuditEntry
```

### 7.3 核心接口

```csharp
public interface IModelRegistry
{
    Task RegisterAsync(AiModelVersion version, CancellationToken ct);
    Task<AiModelVersion?> GetAsync(string modelId, string version, CancellationToken ct);
    Task<IReadOnlyList<AiModelVersion>> ListAsync(string modelId, CancellationToken ct);
}

public interface IModelPackageValidator
{
    Task<ModelValidationResult> ValidateAsync(string packagePath, CancellationToken ct);
}

public interface IModelDeploymentManager
{
    Task PromoteToShadowAsync(ModelDeploymentRequest request, CancellationToken ct);
    Task PromoteToChampionAsync(ModelDeploymentRequest request, CancellationToken ct);
    Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct);
}
```

### 7.4 实现顺序

1. Manifest 和 Hash 契约；
2. 本地路径 containment、大小上限、扩展名和 SHA-256；
3. Registry SQL；
4. Deployment 状态机；
5. Shadow 运行时；
6. Champion/Challenger 评估；
7. Fallback 和 Quarantine；
8. Host API；
9. Desktop 只读/审批页；
10. CI 和文档。

### 7.5 SQL

```text
Wcs_AiModelRegistry
Wcs_AiModelPackage
Wcs_AiModelDeployment
Wcs_AiModelEvaluation
Wcs_AiModelDriftEvent
Wcs_AiModelAuditJournal
```

索引建议：

- `(ModelId, ModelVersion)` 唯一；
- `(ModelId, AssetType, Profile, DeploymentStatus)`；
- `(CreatedAtUtc)`；
- `(CorrelationId)`；
- Champion 通过事务和唯一约束保证同范围唯一。

### 7.6 API

```text
GET  /api/industrial-intelligence/models
GET  /api/industrial-intelligence/models/{id}/versions
POST /api/industrial-intelligence/models/register
POST /api/industrial-intelligence/models/{id}/{version}/validate
POST /api/industrial-intelligence/models/{id}/{version}/shadow
POST /api/industrial-intelligence/models/{id}/{version}/promote
POST /api/industrial-intelligence/models/{id}/{version}/quarantine
POST /api/industrial-intelligence/models/{id}/rollback
```

### 7.7 必测场景

- 相同 Version 不同 SHA 被拒绝；
- ManifestHash 可确定性生成；
- 路径穿越被拒绝；
- 超大包被拒绝；
- FeatureSchema 不匹配被拒绝；
- 输入输出 Shape 不匹配被拒绝；
- 未批准版本不能 Shadow；
- Shadow 不写正式结果和控制链路；
- 同范围最多一个 Champion；
- 新 Champion 激活后旧版本成为 Fallback；
- Champion 加载失败自动回退；
- Fallback 无效时保持现状并报警；
- 重启恢复 Deployment；
- SQL 故障不阻塞控制；
- 20k/100k 推理负载和内存门禁；
- 坏模型隔离后其他模型继续运行。

### 7.8 阶段完成

- ModelOps 专项通过；
- P0+P1 累计 exact-head 回归通过；
- Registry/Deployment/回滚 Evidence 固化；
- 不改变现有 v3.8/v3.9 输出契约；
- 所有模型默认只进入 Shadow。

## 8. IDI-P2：Feature Center

### 8.1 目标

统一特征定义、FeatureSchema、Snapshot、实时/历史物化、质量、血缘和 Point-in-Time Dataset。

### 8.2 核心对象

```text
FeatureDefinition
FeatureSchema
FeatureSchemaItem
FeatureSnapshot
FeatureValue
FeatureSourceOffset
FeatureQualityEvent
FeatureDatasetManifest
FeatureLineageEntry
```

### 8.3 第一批特征范围

优先覆盖已有数据，不新增传感器：

```text
health.latest
health.mean
health.minimum
health.maximum
health.stddev
health.slopePerHour
fusionRisk.mean
fusionRisk.maximum
grade.changeCount
grade.criticalRatio
alarm.activeCount
task.completedCount
vehicle.busyRatio
vehicle.waitSeconds
traffic.conflictCount
maintenance.hoursSinceLast
```

每个特征必须定义单位、窗口、Freshness、NullPolicy 和 ValidRange。

### 8.4 物化方式

- EventBus 增量更新实时特征；
- 定时任务补偿历史聚合；
- Snapshot 在推理或 Proposal 前冻结；
- 历史 Dataset 通过 AsOfUtc 做 Point-in-Time Join；
- 大规模历史导出到 Parquet；
- 控制线程不等待特征物化。

### 8.5 SQL

```text
Wcs_FeatureDefinition
Wcs_FeatureSchema
Wcs_FeatureSchemaItem
Wcs_FeatureSnapshot
Wcs_FeatureQualityEvent
Wcs_FeatureDataset
Wcs_FeatureLineage
```

FeatureValue 可采用：

- 小规模 JSON + ValuesHash；
- 高频数据存 Parquet/时序存储，SQL 保存 URI、时间范围、行数和 SHA；
- 禁止无限增长的逐点 EAV 表直接压垮 SQL Server。

### 8.6 API

```text
GET  /api/industrial-intelligence/features
GET  /api/industrial-intelligence/feature-schemas
GET  /api/industrial-intelligence/feature-snapshots/{id}
POST /api/industrial-intelligence/feature-schemas
POST /api/industrial-intelligence/datasets/build
GET  /api/industrial-intelligence/datasets/{version}
```

### 8.7 必测场景

- DefinitionHash 确定性；
- 单位、窗口、顺序变化导致新版本；
- Freshness 过期返回 Stale；
- NullPolicy Fail/Default/Ignore 正确；
- 越界值产生 QualityEvent；
- Snapshot ValuesHash 确定性；
- 同 AsOfUtc 重放一致；
- Outcome 后数据不能泄漏到 Outcome 前 Dataset；
- FeatureSchema 与 ModelManifest 精确匹配；
- 断电/重启后实时缓存可重建；
- 最大实体数、窗口、行数和查询范围有界；
- SQL/Parquet 故障不阻塞控制。

### 8.8 完成条件

- 当前 v3.9 的固定 14 维特征可迁移为受治理 FeatureSchema；
- 新旧输出双跑一致；
- Point-in-Time 泄漏测试通过；
- Snapshot 可用于模型和 Proposal 重放；
- 累计回归全绿。

## 9. IDI-P3：Shadow Decision

### 9.1 目标

建立标准 DecisionProposal、Explanation、Constraint、Approval 和 Outcome；所有建议先 Shadow，不进入控制写链路。

### 9.2 ProposalType 第一批

```text
MaintenanceWindowRecommendation
AssetLoadReductionRecommendation
VehicleSelectionRecommendation
TaskPriorityRecommendation
StandbyAssetRecommendation
InspectionRecommendation
```

其中车辆选择和任务优先级只记录建议，不修改正式调度结果。

### 9.3 核心接口

```csharp
public interface IDecisionProposalEngine
{
    Task<DecisionProposalResult> EvaluateAsync(
        DecisionContext context,
        CancellationToken ct);
}

public interface IDecisionConstraintEvaluator
{
    Task<IReadOnlyList<ConstraintResult>> EvaluateAsync(
        DecisionContext context,
        DecisionCandidate candidate,
        CancellationToken ct);
}
```

### 9.4 执行顺序

```text
Read-only runtime snapshot
→ FeatureSnapshot
→ Champion + Shadow inference
→ Candidate actions
→ Hard constraint evaluation
→ Impact estimation
→ Explanation
→ Proposal Journal
→ Optional approval
→ External execution result
→ Outcome evaluation
```

### 9.5 SQL

```text
Wcs_DecisionProposal
Wcs_DecisionConstraintResult
Wcs_DecisionApprovalJournal
Wcs_DecisionOutcomeJournal
Wcs_DecisionExplanationEvidence
```

### 9.6 API

```text
GET  /api/industrial-intelligence/proposals
GET  /api/industrial-intelligence/proposals/{id}
POST /api/industrial-intelligence/proposals/{id}/approve
POST /api/industrial-intelligence/proposals/{id}/reject
POST /api/industrial-intelligence/proposals/{id}/outcome
```

审批只改变 Proposal 状态；P3 不自动产生 CommandBus 消息。

### 9.7 必测场景

- 无 FeatureSnapshot 不生成 Proposal；
- 无 Champion 时返回 ModelUnavailable；
- 硬约束可以阻止高分建议；
- 阻止原因完整；
- Explanation 包含模型、特征和规则 Evidence；
- Proposal 幂等；
- 同输入和版本重放一致；
- 过期 Proposal 不能批准；
- 不同 Actor 的批准/拒绝审计正确；
- Shadow Proposal 不改变任务、车辆、路线和 PLC；
- Outcome 可回填并关联实际任务/维修；
- SQL/MES 失败不阻塞控制；
- Proposal 队列和保留期有界。

### 9.8 阶段指标

至少统计：

```text
proposal_generated_total
proposal_blocked_total
proposal_approved_total
proposal_rejected_total
proposal_expired_total
proposal_outcome_matched_total
proposal_estimated_benefit
proposal_actual_benefit
```

## 10. IDI-P4：Maintenance Learning

### 10.1 目标

把维修建议、MES 工单、实际维修、前后特征、预测和设备结果关联，形成可治理的训练标签和效果评估。

### 10.2 数据流

```text
Health/RootCause/Forecast
→ Maintenance Proposal
→ MES Work Order
→ Actual Action
→ Before/After Snapshot
→ Failure/No Failure Observation
→ Effectiveness Evaluation
→ Candidate Training Label
→ Human Approval
```

### 10.3 核心对象

```text
MaintenanceIntervention
MaintenanceOutcome
MaintenanceEffectiveness
CausalCandidate
CounterfactualEstimate
TrainingLabelCandidate
TrainingLabelApproval
```

### 10.4 评价窗口

按资产类型配置：

```text
ImmediateWindow
ShortWindow
MediumWindow
LongWindow
```

禁止使用固定一个窗口覆盖所有设备。窗口配置也要版本化和审批。

### 10.5 必测场景

- 工单重复回调幂等；
- 维修前后 Snapshot 时间顺序正确；
- 无足够观察期返回 Censored；
- 故障发生正确关联；
- 无效维修不会被标记为正样本；
- TrainingLabel 未批准不能进入 Dataset；
- CausalCandidate 明确标记 Estimated；
- 根因人工复核可覆盖模型建议但不覆盖原 Evidence；
- MES 不可用进入 Outbox/Retry；
- Outcome 指标可按模型、资产和维修动作统计。

### 10.6 阶段完成

- 至少完成一个资产类型的闭环样例；
- 维修效果、成本、停机和故障结果可查询；
- 标签审批和 Dataset 血缘可追溯；
- 不自动触发训练和模型激活；
- 累计回归通过。

## 11. IDI-P5：Digital Twin Optimizer

### 11.1 目标

复用 S0～S10 仿真运行时，在固定场景下比较策略 Candidate，输出多目标排名和 Evidence。

### 11.2 第一批策略

```text
CurrentProductionBaseline
ShortestDistance
HealthAware
EnergyAware
SlaAware
BalancedMultiObjective
```

所有策略必须遵守相同硬约束。优化器只能改变软目标评分，不能绕过路权、区段占用和联锁。

### 11.3 实验定义

```text
ExperimentId
ScenarioSetVersion
SeedSet
TopologyRevision
OrderDatasetVersion
PolicyCandidates
ObjectiveWeights
ConstraintProfile
SoftwareHead
```

### 11.4 实验矩阵

每个 Candidate 至少运行：

- 正常负载；
- 峰值负载；
- 单车降级；
- 路段阻塞；
- 外部依赖故障；
- 健康度恶化；
- 重启恢复；
- 固定 Seed 双轮确定性。

### 11.5 指标

```text
Throughput
Mean/P95 MissionLeadTime
Mean/P95 WaitTime
DeadlockCount
ConflictCount
EnergyEstimate
WearIndex
FailureRiskExposure
SlaViolation
RecoveryTime
```

### 11.6 必测场景

- 同 Seed 同版本结果一致；
- Candidate 不能修改硬约束；
- ObjectiveWeights Hash 确定性；
- 缺失指标时不伪造排名；
- 极端权重有上限；
- Baseline 必须包含；
- Candidate 失败不影响其他策略；
- 结果包含 exact Head、PolicyHash、ScenarioHash；
- 只输出推荐和 Evidence；
- 不自动替换生产策略。

### 11.7 完成条件

- 可比较至少 3 个策略 Candidate；
- 双轮确定性通过；
- Stress/Soak 通过；
- 输出多目标 Pareto/排名结果；
- Desktop 只读实验页面可查询；
- 累计 exact-head 回归通过。

## 12. IDI-P6：Bounded Automation Readiness

### 12.1 目标

只建立 L2/L3 软件治理框架和安全准备度，不直接宣称自动控制可投产。

### 12.2 软件能力

```text
AutomationPolicy
ExecutionAllowance
RateLimit
BudgetLimit
MaintenanceWindow
ApprovalRequirement
CircuitBreaker
KillSwitch
RollbackPolicy
```

### 12.3 允许研究的低风险动作

- 维修窗口建议转换为 MES 审批请求；
- 非安全相关的任务优先级建议；
- 备用设备推荐；
- 低负载时间段建议；
- 调度策略 Candidate 的受控 Canary 建议。

### 12.4 永久禁止

- 急停和安全复位；
- 安全门、光栅、机械联锁；
- PLC 强制写；
- 自动解除路权/闭塞；
- 未审批停机；
- 绕过现有状态机和交通约束。

### 12.5 完成条件

- 所有自动化策略默认 Disabled；
- KillSwitch 和回滚通过仿真；
- Rate/Budget/CircuitBreaker 通过；
- 无现场 Evidence 时 Production L2/L3 不能启用；
- 文档明确“software-side ready only”。

## 13. 数据库迁移执行

### 13.1 原则

- 每阶段独立 Migration；
- 只增表/索引，避免直接改现有控制表；
- 新列先 Nullable，再回填，再加约束；
- 大表索引在维护窗口建立；
- Migration 可重复检测；
- 提供回滚脚本，但 Journal 数据默认不删除。

### 13.2 建议顺序

```text
P0 Audit/Evidence
P1 Model Registry/Deployment
P2 Feature Definition/Snapshot/Dataset
P3 Proposal/Approval/Outcome
P4 Maintenance Learning
P5 Experiment/Metric
P6 Automation Policy
```

### 13.3 数据保留

- Registry/审批/审计：长期保留；
- FeatureSnapshot：热存 90 天，之后归档；
- 推理明细：按项目规模配置；
- 大型 Dataset：文件存储，SQL 保存 Manifest；
- Experiment 原始结果：压缩归档；
- 删除必须留 Tombstone/Audit。

## 14. CI/CD 总体设计

每阶段至少建立：

```text
Compile Gate
Contract Gate
Host/SQL Gate
Load/Resource Gate
Determinism Gate
Safety Static Gate
Stage Full Regression
```

### 14.1 固定门禁

- Release 构建；
- 精确测试数量；
- 所有测试 Passed；
- 静态扫描无控制写依赖；
- Production 配置 fail-closed；
- SQL 生命周期正确；
- 重启恢复；
- Artifact 和 Digest；
- exact Head；
- PR Head 未移动。

### 14.2 累计矩阵

建议每阶段在上一阶段 child 数量基础上增加专项，而不是重新发明孤立测试：

```text
S10 Baseline: 46
IDI-P0: 47+
IDI-P1: P0 + ModelOps gates
IDI-P2: P1 + Feature gates
IDI-P3: P2 + Decision gates
IDI-P4: P3 + Maintenance learning gates
IDI-P5: P4 + Optimization gates
```

最终 child 数量在每阶段设计文档中固定，后续不得随意减少。

## 15. 测试分层

### 15.1 单元测试

- Hash；
- 状态机；
- 配置边界；
- 约束；
- 数学输出；
- 幂等；
- Point-in-Time；
- 排名和权重。

### 15.2 集成测试

- SQL；
- 本地模型仓库；
- Parquet；
- Host API；
- MES Outbox；
- 重启恢复。

### 15.3 性能测试

- 20k/100k 推理；
- Feature Materialization；
- 10k Pending Proposal；
- Dataset 构建；
- 多策略实验；
- RSS、GC、句柄、文件和 SQL 连接。

### 15.4 故障测试

- 坏 SHA；
- 模型加载失败；
- SQL 中断；
- 磁盘只读/空间不足；
- MES 超时；
- Feature Stale；
- Candidate 超时；
- 进程重启；
- Duplicate Event；
- Head/Version/Hash 漂移。

## 16. 发布与回滚

### 16.1 发布顺序

```text
Disabled
→ ReadOnly
→ Shadow
→ Limited Asset Scope
→ Wider Shadow
→ L1 Advisory
```

P0～P5 不默认进入 L2/L3。

### 16.2 回滚对象

- 模型 Champion；
- FeatureSchema；
- Decision Policy；
- ObjectiveWeights；
- UI/API 功能开关；
- SQL Migration；
- 应用版本。

### 16.3 回滚要求

- 所有活动对象保留 PreviousStableVersion；
- 回滚不覆盖历史记录；
- 回滚后运行 Smoke + Contract；
- 记录 Actor、Reason、Before/After；
- 控制系统继续使用原确定性策略。

## 17. 权限与审批实施

### 17.1 角色建议

```text
AI Viewer
Feature Engineer
Model Operator
Model Approver
Maintenance Reviewer
Decision Approver
Optimization Engineer
Industrial Intelligence Administrator
```

### 17.2 双人审批

以下操作要求提交人与批准人不同：

- Champion Promote；
- Fallback 删除；
- FeatureSchema 破坏性升级；
- Production 启用；
- L2/L3 等级提升；
- 策略正式切换；
- 阈值放宽超过配置比例。

## 18. 可观测性落地

### 18.1 Trace

每次推理/建议至少产生：

```text
TraceId
ModelVersion
FeatureSnapshotId
ProposalId
OutcomeId
```

### 18.2 Metrics

按模块统一前缀：

```text
wcs_modelops_*
wcs_feature_*
wcs_decision_*
wcs_optimization_*
```

### 18.3 日志

日志禁止打印完整特征向量、敏感资产数据和密钥。使用 ID、Hash、版本和摘要。

## 19. 文档交付模板

每个阶段至少新增三份：

```text
XX-IDI-Px设计说明.md
XX-IDI-Px专项测试报告.md
XX-IDI-Px操作手册.md
```

测试报告必须记录：

```text
Functional Head
Evidence Head
Workflow Run
Test Count
Artifact ID
Artifact Name
Digest
Resource Result
Known Limits
External Pending Items
```

## 20. 建议执行节奏

| 阶段 | 建议重点 | 建议周期 |
|---|---|---|
| P0 | 契约、安全、配置、Evidence | 1 个迭代 |
| P1 | ModelOps | 2～3 个迭代 |
| P2 | Feature Center | 2～3 个迭代 |
| P3 | Shadow Decision | 2 个迭代 |
| P4 | Maintenance Learning | 2 个迭代 + 现场数据观察 |
| P5 | Digital Twin Optimizer | 2～3 个迭代 |
| P6 | 自动化准备度 | 独立安全项目 |

迭代时长由团队决定；不得为赶周期省略 Evidence、资源门禁和回滚。

## 21. 第一阶段可直接执行的 Backlog

### Epic 1：P0 工程骨架

- [ ] 新建 `Wcs.IndustrialIntelligence` 或四个独立项目的决策记录；
- [ ] 新建 Options 和 EnvironmentGuard；
- [ ] 新建 AutomationLevel；
- [ ] 新建 Evidence/VersionedHash 契约；
- [ ] 新建 Audit Journal；
- [ ] 新建只读 Status API；
- [ ] 新建 Production fail-closed 配置；
- [ ] 新建 10～15 条治理测试；
- [ ] 新建 P0 workflow；
- [ ] 新建 P0 设计/测试/操作文档。

### Epic 2：P1 Model Registry

- [ ] ModelDefinition/Version/Manifest；
- [ ] SHA 和路径 containment；
- [ ] SQL Repository；
- [ ] Register/Validate API；
- [ ] immutable version 测试。

### Epic 3：P1 Deployment

- [ ] Candidate/Shadow/Champion/Fallback 状态机；
- [ ] Shadow 独立资源预算；
- [ ] Promote/Rollback；
- [ ] 重启恢复；
- [ ] Quarantine；
- [ ] Champion 唯一约束。

### Epic 4：P1 Evidence

- [ ] 20k/100k 推理负载；
- [ ] RSS/GC 门禁；
- [ ] 坏模型隔离；
- [ ] SQL 生命周期；
- [ ] exact-head Full Regression；
- [ ] Artifact/Digest。

## 22. Definition of Done

每个阶段只有同时满足以下条件才算软件完成：

- [ ] 功能代码完成；
- [ ] API/数据库/配置完成；
- [ ] 权限和审计完成；
- [ ] 单元、集成、负载、故障、重启测试完成；
- [ ] 控制写入为 0 或符合已批准等级；
- [ ] Production fail-closed；
- [ ] 专项 Workflow 全绿；
- [ ] 累计 Full Regression exact-head 全绿；
- [ ] PR Head 未移动；
- [ ] Artifact/Digest 核实；
- [ ] 设计、测试、操作文档和总索引同步；
- [ ] 已知限制和现场待办明确；
- [ ] Squash 合入 `develop`。

## 23. 启动建议

正式开发应从 `IDI-P0` 开始，不要直接编写自动调度优化器。推荐首个分支：

```text
feature/idi-p0-governance-contracts
```

首个阶段只建立治理、契约、环境隔离、Evidence 和 CI 骨架。P0 通过后再进入 ModelOps Center，确保后续 Feature、Decision 和 Optimization 全部建立在统一版本和安全规则上。
