# IDI-P1 ModelOps Center 专项测试报告

## 1. 测试目标

本报告定义 IDI-P1 的软件验收口径。所有完成证据必须绑定同一个 PR exact Head；Head 发生变化后，旧 Run 和旧 Artifact 只作为历史记录，不能继续作为最终完成证据。

P1 不以真实 HIL、机械安全或现场验收作为软件阶段门槛，但继续继承这些外部边界，不允许把 hosted CI、Simulation 或 SQL 集成测试描述为真实现场验收。

## 2. Specialty Gate

Workflow：`.github/workflows/idi-p1-modelops.yml`

最终固定数量：**32 tests**。

### 2.1 基础 16 条

覆盖：

- ManifestHash 确定性与 Schema 变化敏感性；
- 相同版本不同 Hash 拒绝与幂等重复注册；
- Package path traversal、size、artifact SHA、FeatureSchema SHA、shape、ONNX extension；
- 合法受治理 package；
- 未审批版本禁止 Shadow；
- Candidate -> Shadow；
- Champion 必须先 Shadow；
- Champion/Fallback 基础切换与 rollback。

### 2.2 高级 16 条

覆盖：

1. 第三个 Champion 上线后旧 Fallback Retired，只保留一个 Fallback；
2. Champion 被 Quarantine 后不自动提升 Fallback；
3. 重启恢复拒绝重复 Champion；
4. 重启恢复拒绝不存在的 Registry 引用；
5. append-only Audit 拒绝重复 AuditId；
6. Shadow Runtime 只执行 Shadow，并生成 zero-control Evidence；
7. FeatureSchema mismatch fail-closed；
8. Champion/Challenger Evaluation 持久化且禁止自动晋级；
9. Drift 未越阈值不生成事件；
10. Drift 越阈值只生成 Evidence；
11. SQL Registry 跨实例恢复；
12. SQL Registry 相同版本不同 Hash 拒绝；
13. SQL Deployment 重启恢复 Champion/Fallback；
14. SQL 三次晋级仍严格单 Champion/单 Fallback；
15. SQL append-only Audit unique constraint；
16. SQL Quarantine 与 Audit 跨实例恢复。

Workflow 同时执行 Host 与 Desktop Release build，并扫描 ModelOps 领域项目，禁止 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic、RouteReservation、SQL Client 和 Network Client 依赖。

## 3. SQL 集成环境

Specialty workflow 使用独立 SQL Server 2022 service container 和隔离数据库 `WCS_IdiP1`。测试不连接现场数据库，不包含现场密码或生产数据。

验证内容包括：

- 6 张 ModelOps 表可创建；
- Registry `ManifestJson` 可完整恢复；
- Registry Version 唯一；
- Deployment Version/Scope 唯一；
- Champion Scope filtered unique index；
- Fallback Scope filtered unique index；
- Serializable 事务用于关键 Registry/Deployment 状态改变；
- Audit、Evaluation、Drift 为追加证据路径。

## 4. Zero-Control Gate

P1 必须继续满足：

```text
MaximumAutomationLevel <= L1
ControlWriteAllowed = false
AutoPromotionAllowed = false
ProductionAutomationAllowed = false
```

`Wcs.ModelOps` 不能引用任何控制 mutation 类型。Host `/api/modelops` 的 POST 只改变 ModelOps 治理状态，不改变 PLC、设备命令、任务状态、路权或交通控制。

## 5. 累计 Full Regression

Workflow：`.github/workflows/idi-p1-full-regression.yml`

固定为 **48 child**：

```text
P0 cumulative software catalog = 47
P1 ModelOps Contract           = 1
Total                          = 48
```

每个 child 必须满足：

- `head_sha == EXPECTED_SHA`；
- `status == completed`；
- `conclusion == success`。

聚合 Evidence 必须满足：

```text
workflowCount = 48
allSuccess = true
maximumAutomationLevel = L1
controlWriteAllowed = false
autoPromotionAllowed = false
productionAutomationAllowed = false
realHilGateIncluded = false
```

矩阵必须包含成功的：

- `WCS IDI P0 Governance Contract`；
- `WCS IDI P1 ModelOps Contract`；
- `WCS One Hour Soak Load`。

## 6. 长稳验收原则

P1 不因自身新增模型管理能力绕过 WCS 既有 One Hour Soak。累计回归中的 One Hour Soak 必须在最终 exact Head 上为 `success`。

如果 Soak 失败，只允许基于实际 Evidence 修复实现或合理的判定缺陷；不得通过删除 Soak、把 failure 当 success、改用旧 Head Run 或取消资源门槛来伪造通过。

## 7. Artifact 与 Digest

最终完成时必须核对两类 Artifact：

1. `wcs-idi-p1-modelops-<run_number>`；
2. `wcs-idi-p1-full-regression-<run_number>`。

每个 Artifact 必须记录并核实：

- Artifact ID；
- Artifact Name；
- `expired=false`；
- SHA-256 Digest；
- workflow run 的 `head_sha` 等于最终 PR exact Head。

Artifact ID 与 Digest 由最终 Run 动态产生，因此不在本文硬编码历史 Run；最终收口记录以 GitHub Actions Artifact API 和 exact-head Evidence 为准。

## 8. 完成判定

仅当以下所有项同时成立时可把 P1 标记为 `COMPLETED (software-side)`：

- 32/32 Specialty；
- 48/48 Full Regression，`allSuccess=true`；
- PR Head 未漂移；
- SQL Registry/恢复、严格 Champion/Fallback、Quarantine/Rollback、Audit/Evidence、Shadow、Evaluation/Drift 均存在并测试通过；
- Host/Desktop 已完成且仍是 zero-control；
- P0 Production fail-closed 与 L1 上限保持；
- 94～96 与总索引同步；
- Specialty 与 Full Regression Artifact ID/Name/Expiry/Digest 已核实；
- PR 按仓库规则 Ready，并在最终验收后 Squash 合入 `develop`。

真实 HIL、Protocol、Mechanical Safety 和 Site Acceptance 继续保留为独立现场 Evidence，不阻塞 P1 软件完成通知。
