# IDI-P5 Digital Twin Optimizer 操作手册

## 1. 使用范围

本手册用于开发、测试、审计人员查看 IDI-P5 数字孪生优化实验和 Evidence。P5 不是生产自动调度入口。

固定安全边界：

```text
MaximumAutomationLevel <= L1
ControlWriteAllowed = false
AutoProductionPolicyReplacementAllowed = false
ProductionAutomationAllowed = false
```

## 2. 正式实验输入要求

创建正式 Definition 的软件流程必须提供：

- 唯一 `ExperimentId`；
- `ScenarioSetVersion`；
- 非零且不重复的 `SeedSet`；
- `TopologyRevision`；
- `OrderDatasetVersion`；
- 3～12 个 Candidate；
- 恰好一个 `CurrentProductionBaseline`；
- `ObjectiveWeights`；
- SHA-256 `ConstraintProfileHash`；
- exact Git `SoftwareHead`；
- 每个 Candidate 的 `Version / ApprovedBy / ApprovedAtUtc`。

推荐使用 `OptimizationPolicyCatalog.CreateStandardCandidates(...)` 生成六策略标准目录，再按实验需要选择至少 3 个 Candidate。

## 3. 实验执行原则

实验执行属于软件内部受控流程，不从 Host HTTP 暴露。

每个 Candidate 必须对相同输入执行：

```text
8 LoadCases × SeedSet × 2 Determinism Rounds
```

禁止为不同策略使用不同 topology、orders、constraint 或 seed 以制造排名优势。

任何 Hard Constraint 失败时，不允许通过提高 Objective Weight 把该 Candidate 重新排成有效策略。

## 4. 查看 Host Evidence

只读接口：

```text
GET /api/digital-twin-optimizer/status
GET /api/digital-twin-optimizer/policy-kinds
GET /api/digital-twin-optimizer/experiments?limit=100
GET /api/digital-twin-optimizer/experiments/{experimentId}/definition
GET /api/digital-twin-optimizer/experiments/{experimentId}/result
```

如果 Industrial Intelligence 环境边界不允许，接口返回 404；SQL Evidence 故障时 fail-closed 返回 503。

没有以下 API：

```text
POST execute
POST apply-policy
POST replace-production-policy
POST dispatch
POST route/reservation
POST plc-write
```

## 5. Avalonia 页面

Desktop 菜单：

```text
IDI-P5 Digital Twin Optimizer
```

页面操作：

1. 点击“刷新只读 Evidence”；
2. 查看 Environment、Mode/L1、SQL Recovery；
3. 查看最近实验；
4. 输入 `ExperimentId`；
5. 点击“读取 Result”；
6. 查看 Result EvidenceHash、Runs、Candidate Ranking、Pareto 和 HardConstraintQualified。

页面没有策略应用按钮。

## 6. Evidence 审计

重点核对：

- DefinitionHash；
- SoftwareHead；
- Scenario/Topology/OrderDataset/ObjectiveWeights/ConstraintProfile Evidence Hash；
- PolicyHash；
- Run ScenarioHash；
- Run FinalStateHash；
- Run EvidenceHash；
- S0～S10 StageEvidenceHash；
- Result EvidenceHash；
- `ControlWriteAllowed=false`；
- `AutoProductionPolicyReplacementAllowed=false`；
- `ProductionAutomationAllowed=false`。

同一 `PolicyId + LoadCase + Seed` 的 Round 1 / Round 2 必须拥有相同 FinalStateHash、Metrics 和 Hard Constraint 结果。

## 7. CI 验收顺序

最终 Acceptance Head 冻结后按以下顺序核对：

1. `WCS IDI P5 Digital Twin Optimizer Contract` — 24/24；
2. `WCS IDI P5 Optimizer Stress Soak` — 三轮 integration + S8 12/12；
3. `WCS One Hour Soak Load` — success；
4. `WCS IDI P5 Full Regression` — exact-head 全 child success；
5. Specialty/Stress/Full Regression Artifact；
6. Artifact `expired=false`；
7. Digest/SHA-256；
8. Artifact Evidence `head_sha`；
9. PR `expected_head_sha == actual head`；
10. PR Ready + Squash Merge `develop`。

不要在 final matrix 运行期间继续提交代码；任何文档 Evidence 更新形成新 head 后，必须重新跑同一 final exact-head gate。

## 8. 真实 HIL 与生产边界

P5 S9 Stage Evidence 必须保持：

```text
RequiresRealHardware = true
Executed = false
RealHardwareExecuted = false
```

P5 不能把 Simulation、GitHub Hosted CI、手工 JSON 或 Mock 证据替代现场 HIL。

P5 完成也不意味着：

- PLC 安全通过；
- 机械安全通过；
- 现场协议通过；
- Site Acceptance 通过；
- Production L2/L3 可启用。

这些边界继续 fail-closed。
