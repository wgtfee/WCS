# IDI-P5 Digital Twin Optimizer 设计说明

## 1. 目标

IDI-P5 在既有 WCS Simulation & Verification S0～S10 软件能力上建立**推荐型数字孪生优化器**。它只生成 Candidate 对比、Pareto/多目标排名和不可变 Evidence，不接管生产调度，不写现场控制链路。

P5 的安全上限永久保持：

```text
MaximumAutomationLevel <= L1
ControlWriteAllowed = false
AutoProductionPolicyReplacementAllowed = false
ProductionAutomationAllowed = false
```

P5 不修改 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic、RouteReservation 等生产控制事实源。

## 2. 模块

- `Wcs.Optimization`
  - `OptimizationContracts`
  - `OptimizationPolicyCatalog`
  - `DigitalTwinOptimizer`
  - `OptimizationPersistenceContracts`
- `Wcs.Simulator/Optimization`
  - `GovernedDigitalTwinExperimentRunner`
- `Wcs.Infrastructure/IndustrialIntelligence`
  - Optimization SQL Entities / Store / Recovery
- `Wcs.Host/Controllers/DigitalTwinOptimizerController`
  - 只读 GET Evidence API
- `Wcs.Desktop`
  - `DigitalTwinOptimizerApiService`
  - `DigitalTwinOptimizerViewModel`
  - `DigitalTwinOptimizerView`

## 3. 正式 Candidate

标准 Candidate Catalog 固化六种策略类型：

1. `CurrentProductionBaseline`
2. `ShortestDistance`
3. `HealthAware`
4. `EnergyAware`
5. `SlaAware`
6. `BalancedMultiObjective`

每次正式实验至少比较 3 个 Candidate，并且必须恰好包含一个 `CurrentProductionBaseline`。Candidate 只改变数字孪生实验中的软目标投影，不具有生产策略替换能力。

## 4. 输入一致性与 Evidence

同一实验的所有 Candidate 强制共享：

- `ScenarioSetVersion`
- `SeedSet`
- `TopologyRevision`
- `OrderDatasetVersion`
- `ConstraintProfileHash`
- `SoftwareHead`
- `ObjectiveWeights`

Definition 同时固化 SHA-256：

- `ScenarioEvidenceHash`
- `TopologyEvidenceHash`
- `OrderDatasetEvidenceHash`
- `ObjectiveWeightsEvidenceHash`
- `ConstraintProfileHash`
- 每个 `PolicyHash`
- `DefinitionHash`

结果固化 `ScenarioHash`、`FinalStateHash`、Run `EvidenceHash`、S0～S10 `StageEvidence` 和最终 Result `EvidenceHash`。

## 5. S0～S10 正式集成

`GovernedDigitalTwinExperimentRunner` 复用已有软件侧仿真体系：

| Stage | P5 行为 |
|---|---|
| S0 | 治理、Definition/Scenario/Constraint Evidence，只读边界 |
| S1 | Deterministic State / Seed / Scenario Engine 证据 |
| S2 | 通过现有集成/容量 Runtime 使用 Virtual PLC |
| S3 | 使用 Virtual RGV |
| S4 | 使用 Virtual Traffic / Reservation / Conflict / Deadlock 软件模型 |
| S5 | 使用 Virtual External 软件依赖模型 |
| S6 | 使用 Virtual Health 软件模型 |
| S7 | 使用 Integrated Recovery Mission 链路 |
| S8 | 使用 CapacityReadinessRuntime，验证 Admission、Conservation、Bounded State |
| S9 | **仅软件边界**；`Executed=false`、`RealHardwareExecuted=false` |
| S10 | 统一验证只读边界；Remote Control 不开放 |

P5 不把 GitHub Hosted CI、Simulator 或 Mock 证据声明成真实 HIL。

## 6. Load Case

每个 Candidate × Seed 都必须覆盖：

- `NormalLoad`
- `PeakLoad`
- `SingleVehicleDegraded`
- `SegmentBlocked`
- `ExternalDependencyFailure`
- `HealthDegraded`
- `RestartRecovery`
- `DeterminismReplay`

每个输入组合执行 **2 轮**。双轮必须保持相同 `FinalStateHash`、Metrics 和 Hard Constraint 结果；否则整个实验 fail-closed。

## 7. Hard Constraint

Hard Constraint 不是评分项，不能被 Objective Weight 抵消。任何一个 Run 失败，整个 Policy Candidate 都失去排名资格：

```text
HardConstraintQualified = false
Score = -1
ParetoEfficient = false
```

有效 Candidate 必须满足 S0～S10 Stage Evidence 的 Hard Constraint 合取关系。S9 的“真实硬件未执行”不是绕过，而是 P5 软件阶段的显式外部边界。

## 8. 多目标与 Pareto

评分维度：

- Throughput（越高越好）
- P95 Mission Lead Time（越低越好）
- P95 Wait Time（越低越好）
- Energy（越低越好）
- Wear（越低越好）
- Failure Risk（越低越好）
- SLA Violation（越低越好）
- Recovery Time（越低越好）

仅完全通过 Hard Constraint 的 Candidate 参与归一化与 Pareto 判断。ObjectiveWeights 有独立 Evidence Hash，避免无痕改变评分偏好。

## 9. SQL Evidence

SQL 表：

- `Wcs_OptimizationExperiment`
- `Wcs_OptimizationExperimentResult`
- `Wcs_OptimizationPolicyEvidence`
- `Wcs_OptimizationRunEvidence`

`Wcs_OptimizationRunEvidence` 唯一键：

```text
ExperimentId + PolicyId + LoadCase + Seed + DeterminismRound
```

保存 PolicyHash、ScenarioHash、FinalStateHash、EvidenceHash、MetricsJson、StageEvidenceJson 和 Hard Constraint 状态。Recovery 会重新验证 Definition/Result Hash、zero-control 状态和 Run Evidence 数量。

## 10. Host / Desktop

Host 仅提供 GET：

```text
GET /api/digital-twin-optimizer/status
GET /api/digital-twin-optimizer/policy-kinds
GET /api/digital-twin-optimizer/experiments
GET /api/digital-twin-optimizer/experiments/{id}/definition
GET /api/digital-twin-optimizer/experiments/{id}/result
```

没有 HTTP Experiment Execute、Apply Policy、Dispatch、Route、Reservation 或 PLC 写入口。

Avalonia 页面只读取状态、最近实验、Result Evidence、Ranking/Pareto。页面不提供“应用策略”“替换生产策略”或任何设备控制按钮。

## 11. 完成门禁

P5 只有在同一 exact Acceptance Head 上同时满足以下条件才可 Ready + Squash Merge：

- P5 Specialty 固定数量全绿；
- P5 Stress/Soak 全绿；
- One Hour Soak 全绿；
- cumulative Full Regression 全部 child 为 exact head 且 success；
- Specialty/Stress/Full Regression Artifact `expired=false`；
- Artifact Digest/SHA-256 校验通过；
- `head_sha` 与 PR expected/actual 一致；
- 三份 P5 文档和总索引完成；
- 无安全边界降级。
