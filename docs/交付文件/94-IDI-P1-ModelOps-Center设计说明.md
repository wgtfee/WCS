# IDI-P1 ModelOps Center 设计说明

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 阶段 | Industrial Decision Intelligence v4.0 — IDI-P1 |
| 能力 | ModelOps Center |
| 分支 | `feature/idi-p1-modelops-center` |
| 基线 | IDI-P0 已完成并 Squash 合入 `develop` |
| 自动化上限 | L1 |
| 控制写 | 永久关闭于 P1；`ControlWriteAllowed=false` |
| Production | 继承 P0 fail-closed，不开放 Production 自动化 |

P1 的目标不是新增一条“AI 控制链路”，而是把模型的注册、包校验、部署状态、Shadow 执行、Champion/Challenger 评估、漂移 Evidence、回滚、隔离和审计做成受治理的软件能力。P1 不修改 EMS/RGV 交通控制语义，不改变 AnomalyEngine v3.8/v3.9 既有输出契约，也不允许模型绕过 PLC、状态机、路权或安全联锁。

## 2. 架构边界

P1 新增独立领域项目 `Wcs.ModelOps`。该项目保持无 SQL Client、无网络 Client、无 PLC/CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic/RouteReservation mutation 依赖。

```text
Desktop ModelOps approval surface
          |
          v
Host /api/modelops
          |
          v
Infrastructure SQL adapters
          |
          v
Wcs.ModelOps domain contracts / invariants

Wcs.ModelOps  -X-> PLC
Wcs.ModelOps  -X-> CommandBus
Wcs.ModelOps  -X-> TaskScheduler / TaskOrchestrator
Wcs.ModelOps  -X-> DeviceManager
Wcs.ModelOps  -X-> Traffic / Route reservation mutation
```

SQL 或模型服务异常只能让 ModelOps 自己 fail-closed；不得阻塞 WCS 控制主线程。

## 3. 模型不可变契约

`AiModelPackageManifest` 固定包含：ModelId、ModelVersion、ModelType、ArtifactFile、ArtifactSha256、ManifestHash、FeatureSchemaId/Hash、TrainingDatasetVersion/Hash、训练资产/失败事件计数、验证指标、RuntimeLimits、审批人/审批时间、FallbackVersion、InputShape、OutputShape。

`ModelManifestHash.Compute` 对规范化 Manifest 计算确定性 SHA-256。相同 `ModelId + ModelVersion` 只能对应同一个 ManifestHash；相同版本不同 Hash 必须拒绝，而不是覆盖。

Package Validator 校验：

- 包目录必须存在且不能通过 `..`、symlink/reparse point 越界；
- 模型 artifact 必须为 `.onnx`；
- Package 总大小有界；
- Artifact SHA-256 与 FeatureSchema SHA-256 必须匹配；
- Input/Output shape 必须为正维度；
- Runtime inference 和 working-set 上限必须在允许范围；
- 进入 Shadow 前必须已有显式审批。

## 4. SQL-backed Registry 与恢复

P1 使用 SQL Server 保存可恢复治理状态，并由 Infrastructure 适配，不把 SQL 引入 `Wcs.ModelOps` 领域项目。

正式表：

1. `Wcs_AiModelRegistry`
2. `Wcs_AiModelPackage`
3. `Wcs_AiModelDeployment`
4. `Wcs_AiModelEvaluation`
5. `Wcs_AiModelDriftEvent`
6. `Wcs_AiModelAuditJournal`

Registry 保存完整 `ManifestJson` 和 `ManifestHash`。`UX_Wcs_AiModelRegistry_ModelVersion` 保证版本唯一；注册使用 Serializable 事务避免并发同版本覆盖。

Deployment 使用 `(ModelId, ModelVersion, AssetType, Profile)` 唯一键，并使用 filtered unique index 保证同一 `(ModelId, AssetType, Profile)` 最多一个 Champion、最多一个 Fallback。`ModelDeploymentRecoveryService` 在重启后从持久层验证：

- Champion/Fallback 数量不冲突；
- 活跃 deployment 对应 Registry 版本存在；
- 活跃版本仍满足审批要求。

出现不一致时恢复服务必须 fail-closed，禁止“猜一个 Champion”。

## 5. 部署状态机

P1 的治理路径为：

```text
Candidate -> Shadow -> Champion
                    previous Champion -> Fallback
older Fallback -> Retired
Champion/Fallback/Shadow -> Quarantined (explicit operation)
Fallback -> Champion (explicit rollback)
```

规则：

- Champion 必须先经历 Shadow；
- 新 Champion 上线时，旧 Champion 成为唯一 Fallback；
- 若此前已有旧 Fallback，则先 Retired，避免 Fallback 累积；
- Rollback 只能显式执行，并要求 Fallback 仍可用、仍已审批；
- Quarantine Champion 后不自动把 Fallback 提升为 Champion，系统保持 fail-closed，等待人工治理动作；
- P1 没有 auto promotion、auto fallback promotion 或 auto control。

## 6. Shadow Runtime

`GovernedShadowRuntime` 只枚举 `Shadow` deployment，不执行 Champion 控制，也不产生 WCS 控制命令。执行前要求：

- Registry 版本存在；
- 版本已审批；
- `FeatureSchemaId` 精确匹配；
- inference 时间不超过 Manifest RuntimeLimits；
- runner 返回合法 Evidence SHA-256。

每次 Shadow 结果写入 `ShadowInferenceRecord`，其中 `ControlWriteAllowed=false`。P1 的 Shadow Output 仅用于比较和 Evidence，不改写 v3.8/v3.9 对外预测契约。

## 7. Champion / Challenger 与 Drift

`ChampionChallengerEvaluator` 对固定 DatasetHash 的观察样本计算比较指标并持久化 `AiModelEvaluation` Evidence。即使 Challenger 指标更好，`AutoPromotionAllowed=false`，只能由人工批准进入 Champion。

`ModelDriftMonitor` 对超过阈值的漂移生成 `AiModelDriftEvent` 和 Evidence SHA-256；P1 不允许自动隔离或自动控制。Drift Event 是治理输入，不是控制命令。

## 8. Audit / Evidence

ModelOps 状态改变都必须记录 Actor、Reason、CorrelationId 和 PayloadHash。`Wcs_AiModelAuditJournal` 为 append-only Journal，AuditId 唯一；重复 ID 由数据库和领域测试共同拒绝。

关键动作至少包含：

- `PromoteToShadow`
- `PromoteToChampion`
- `RollbackToFallback`
- `Quarantine`
- `QuarantineChampionFailClosed`

## 9. Host API

P1 新增 `/api/modelops`。它受 P0 `IndustrialIntelligenceEnvironmentGuard` 与 `MaximumAutomationLevel<=L1` 共同约束。

读取接口包括 Status、Registry、Deployments、Audit、Evaluations、Drift。写接口仅改变 ModelOps 治理元数据：Register、Shadow、Champion、Rollback、Quarantine。

这些 POST 不是 PLC/调度控制接口；响应明确声明 `controlWriteAllowed=false`。Production 仍继承 P0 fail-closed。

## 10. Desktop

Desktop 增加 `IDI-P1 ModelOps Center` 页面：

- 查看环境与 Recovery 状态；
- 查看 Scope 内 Shadow/Champion/Fallback/Quarantined；
- 查看 append-only Audit；
- 输入 Actor/Reason 后进行人工 Shadow、Champion、Rollback、Quarantine 治理操作。

页面不提供 PLC、设备、路权、调度或安全联锁控制入口。

## 11. 软件完成定义

P1 软件完成必须在同一个未漂移 exact Head 上满足：

```text
WCS IDI P1 ModelOps Contract = exactly 32/32 success
WCS IDI P1 Full Regression   = exactly 48/48 completed/success
workflowCount                 = 48
allSuccess                    = true
ControlWriteAllowed           = false
AutoPromotionAllowed          = false
ProductionAutomationAllowed   = false
MaximumAutomationLevel        <= L1
PR Head verification          = expected == actual
P1 Specialty Artifact/Digest  = verified and not expired
P1 Full Regression Artifact/Digest = verified and not expired
```

48-child 为 P0 已固定的 47 个软件工作流，加 P1 `WCS IDI P1 ModelOps Contract` 1 个工作流。真实 HIL、机械安全和 Site Acceptance 不是 P1 软件完成门槛，也不能被 P1 软件 Evidence 冒充。
