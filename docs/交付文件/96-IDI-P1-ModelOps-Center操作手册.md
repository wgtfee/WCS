# IDI-P1 ModelOps Center 操作手册

## 1. 适用范围

本手册用于 IDI-P1 ModelOps Center 的软件部署、模型注册、Shadow、Champion/Fallback、Rollback、Quarantine、Recovery、Audit、Desktop 与 CI 验证。

P1 的所有操作都是“模型治理操作”，不是 EMS/RGV 控制操作。P1 不允许直接写 PLC、修改任务状态、改变设备命令、释放路权或覆盖安全联锁。

## 2. 环境与安全前提

P1 继承 `IndustrialIntelligenceEnvironmentGuard`。只有 P0 Governance 允许的环境才能访问 `/api/modelops`，并且 `EffectiveMaximumAutomationLevel` 必须不高于 L1。

Production 继续 fail-closed。若 Governance 不允许，Host 返回 NotFound，而不是降级为隐式开放。

关键安全值：

```text
MaximumAutomationLevel = L1 or lower
ControlWriteAllowed = false
AutoPromotionAllowed = false
ProductionAutomationAllowed = false
```

## 3. 数据库

P1 使用 `ConnectionStrings:WcsDb`，由 `ModelOpsPersistenceFactory` 在首次 ModelOps API 使用时执行幂等 Schema Bootstrap。这样数据库或 ModelOps 故障不会加入 WCS 控制启动的关键路径。

表：

- `Wcs_AiModelRegistry`
- `Wcs_AiModelPackage`
- `Wcs_AiModelDeployment`
- `Wcs_AiModelEvaluation`
- `Wcs_AiModelDriftEvent`
- `Wcs_AiModelAuditJournal`

部署前检查 filtered unique indexes 已存在：

```text
UX_Wcs_AiModelRegistry_ModelVersion
UX_Wcs_AiModelDeployment_VersionScope
UX_Wcs_AiModelDeployment_ChampionScope
UX_Wcs_AiModelDeployment_FallbackScope
```

不要直接在 SQL 中手工修改 Champion/Fallback。若数据库状态被人工破坏，Recovery 必须 fail-closed；应先修复数据并留下变更审计，再恢复 ModelOps 操作。

## 4. 模型包准备

标准 package 至少包含：

```text
manifest.json
model.onnx
feature-schema.json
normalization.json
validation-evidence.json
```

Manifest 必须保存真实的 SHA-256，而不是文件名或短 Hash。相同 ModelId + ModelVersion 一旦注册，不允许换模型文件、FeatureSchema、Dataset 或审批元数据后继续使用原版本号。

若需要改变任意不可变内容，创建新 ModelVersion。

## 5. 注册流程

调用：

```text
POST /api/modelops/registry
```

提交完整 `AiModelPackageManifest`、Actor、Reason、CorrelationId。

成功后版本处于 Candidate 治理语义。重复提交相同 ManifestHash 为幂等；相同版本但不同 ManifestHash 返回 conflict。

推荐操作记录：

```text
Actor: 真实操作者/服务身份
Reason: 为什么注册此模型版本
CorrelationId: 本次变更单或流水标识
```

不要使用空 Actor、空 Reason 或共享固定 CorrelationId。

## 6. Shadow

调用：

```text
POST /api/modelops/deployments/shadow
```

前提：

- Registry 版本存在；
- Manifest 已审批；
- 当前版本未被 Quarantine；
- Scope = ModelId + AssetType + Profile 明确。

Shadow Runtime 只生成推理结果与 Evidence，不影响 WCS 控制输出。FeatureSchema 不匹配、版本不存在、超时或 Evidence Hash 非法时应 fail-closed。

## 7. Champion

调用：

```text
POST /api/modelops/deployments/champion
```

只能把已经处于 Shadow 的版本人工批准为 Champion。

晋级结果：

- 新版本 -> Champion；
- 旧 Champion -> Fallback；
- 更旧的 Fallback -> Retired；
- 同一 Scope 任何时刻最多一个 Champion 和一个 Fallback。

“Challenger 指标更好”不是自动晋级条件。P1 明确 `AutoPromotionAllowed=false`。

## 8. Rollback

调用：

```text
POST /api/modelops/deployments/rollback
```

Rollback 是人工治理动作。只有 Scope 内存在有效、已审批 Fallback 时才允许：

```text
Fallback -> Champion
old Champion -> Fallback
```

Fallback 不存在、Registry 不存在或审批不成立时必须拒绝，不允许自动选择任意旧版本。

## 9. Quarantine

调用：

```text
POST /api/modelops/deployments/quarantine
```

适用于 package/evidence/drift/运行异常等需要隔离的版本。

如果隔离当前 Champion，P1 不自动把 Fallback 晋级为 Champion：

```text
Champion -> Quarantined
Fallback remains Fallback
Champion count -> 0
```

这种状态是设计上的 fail-closed，需要明确人工 Rollback 或新的 Shadow -> Champion 审批。

## 10. Recovery

读取：

```text
GET /api/modelops/status
```

关注：

- `recoveryHealthy`
- `recoveryErrors`
- `championCount`
- `fallbackCount`
- `shadowCount`
- `quarantinedCount`

Recovery 发现重复 Champion/Fallback、Missing Registry Reference 或未审批活跃版本时返回 unhealthy。不要通过跳过 Recovery 校验继续操作。

## 11. Audit / Evaluation / Drift

读取接口：

```text
GET /api/modelops/audit?modelId=<id>&limit=<1..500>
GET /api/modelops/evaluations/<modelId>?limit=<1..500>
GET /api/modelops/drift/<modelId>?limit=<1..500>
```

Audit Journal append-only。不要 UPDATE/DELETE 历史 Audit 记录来“修正”操作；错误记录应追加新的纠正记录。

Evaluation 和 Drift 都是 Evidence。它们不自动改变 Champion、不自动 Quarantine、不产生控制命令。

## 12. Desktop

Desktop 菜单：`IDI-P1 ModelOps Center`。

页面提供：

- Environment / Mode / Recovery 状态；
- Scope Deployment 表；
- Append-only Audit 表；
- 手工 Enter Shadow；
- 手工 Approve Champion；
- 手工 Rollback Fallback；
- 手工 Quarantine。

每个写治理动作必须填写 Actor 和 Reason。页面提示“无 PLC 控制”属于安全契约，不得在后续 UI 改成自动执行控制。

## 13. CI 操作

Specialty：

```text
WCS IDI P1 ModelOps Contract
expected tests = 32
```

它会启动隔离 SQL Server、Build Host/Desktop、运行 32 tests，并检查 SQL schema、Recovery、Shadow/Evaluation/Drift、Host/Desktop 与 zero-control boundary。

累计回归：

```text
WCS IDI P1 Full Regression
expected child workflows = 48
```

Full Regression 复用或 dispatch 同一 branch exact Head 的 48 个软件 workflow，并最终验证 PR Head 未漂移。

## 14. 排错

### 14.1 Registry conflict

现象：相同 ModelId/Version 不同 ManifestHash。

处理：不要覆盖旧版本；修正 Version，重新生成 ManifestHash，再注册。

### 14.2 Recovery unhealthy

处理顺序：

1. 查看 Recovery Errors；
2. 查看 Deployment 和 Registry；
3. 查看 Audit CorrelationId；
4. 修复治理数据；
5. 再次运行 Recovery；
6. 不要在 unhealthy 状态下强行选择 Champion。

### 14.3 SQL unavailable

ModelOps API 返回 503/fail-closed。WCS 控制主链路应继续独立运行。恢复 SQL 后重新读取 Status，不需要为了 ModelOps 故障重写 PLC 状态。

### 14.4 Shadow inference timeout

检查 Manifest `MaximumInferenceMilliseconds`、runner 性能和输入 FeatureSchema。不要通过接入控制线程或无限放大 timeout 来掩盖问题。

### 14.5 One Hour Soak failure

读取该 exact Head 的 Soak Artifact 和 GC/RSS Evidence。必须修复真实稳定性问题或证明并修正错误的判定逻辑；不得从 48-child 中删除 Soak，也不得复用旧 Head 的成功 Run。

## 15. 最终收口顺序

1. 代码、94～96 文档和总索引全部停止变更；
2. 记录 PR exact Head；
3. `WCS IDI P1 ModelOps Contract` 在 exact Head 达到 32/32 success；
4. `WCS IDI P1 Full Regression` 在同一 Head 达到 48/48 completed/success、`allSuccess=true`；
5. 核实 Production fail-closed、L1 和 zero-control；
6. 核实 Specialty Artifact ID/Name/expired/Digest；
7. 核实 Full Regression Artifact ID/Name/expired/Digest；
8. 再次确认 PR Head 未移动；
9. PR Mark Ready；
10. 按完成规则 Squash Merge 到 `develop`；
11. 记录 merge commit，并把 P1 标记 `COMPLETED (software-side)`。

真实 HIL、Protocol、Mechanical Safety 和 Site Acceptance 保持独立现场验收，不阻塞第 11 步的软件完成判定。
