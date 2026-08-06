# IDI-P5 Digital Twin Optimizer 专项测试报告

## 1. 测试目标

本报告验证 IDI-P5 是否满足以下软件侧门禁：

- 数字孪生优化仅用于推荐和 Evidence；
- 至少 3 个策略 Candidate，并支持六种标准 Policy Kind；
- 所有 Candidate 使用相同 Scenario/Seed/Topology/OrderDataset/ConstraintProfile；
- 每个 `Policy × LoadCase × Seed` 同 Seed 双轮确定性；
- Hard Constraint 失败后 Candidate 不可继续参与有效排名；
- ObjectiveWeights/Policy/Scenario/Topology/OrderDataset/ConstraintProfile/SoftwareHead Evidence 可验证；
- S0～S10 软件链路 Evidence 完整；
- SQL Evidence 可恢复；
- Host/Desktop 只读；
- Stress/Soak、One Hour Soak 和 cumulative exact-head regression 不降级。

## 2. Specialty 固定集

P5 Specialty 最终固定为 **24 tests**：

- 18 个 `DigitalTwinOptimizerContractTests`；
- 6 个 `DigitalTwinOptimizerIntegrationTests`。

覆盖：

- L1 / zero-control；
- minimum 3 candidates；
- exactly one production baseline；
- ConstraintProfile drift reject；
- exact SoftwareHead；
- deterministic policy/experiment hashes；
- all required inputs run twice；
- determinism mismatch reject；
- any hard-constraint failure disqualifies whole candidate；
- Pareto evidence；
- required load cases；
- objective/scenario/topology/order evidence hashes；
- exact S0～S10 evidence；
- missing stage reject；
- real HIL claim reject；
- six policy kinds；
- governed actual simulator runner；
- cross-policy same ScenarioHash；
- three-candidate and six-candidate actual evaluation。

Final CI 必须显式验证：

```text
Total tests: 24
Passed:      24
Failed:      0
```

## 3. Stress / Soak

独立 `WCS IDI P5 Optimizer Stress Soak` gate：

1. Release build `Wcs.Simulator.Tests`；
2. 六策略实际 integration matrix 连续执行 3 次；
3. 每次严格 6/6；总计 18 次 integration test pass；
4. 复用未降低阈值的 `SimulationCapacityReadinessTests`，严格 12/12；
5. 其中包含现有 S8 虚拟 8h 和虚拟 24h soak；
6. 静态扫描禁止 P5 Optimization 引用真实控制客户端或控制事实源。

该 gate 不替代全局 `WCS One Hour Soak Load`。最终 cumulative Full Regression 必须同时包含独立 One Hour Soak child。

## 4. SQL Evidence 验证点

正式 Result 持久化前必须重新计算：

```text
Result.EvidenceHash == ComputeResultEvidenceHash(Definition, Runs)
```

持久化内容包括：

- Definition Evidence Hashes；
- Result Evidence Hash；
- 每个 Policy Rank / Score / Pareto / HardConstraintQualified / Aggregate；
- 每个 Run 的 LoadCase / Seed / Round / ScenarioHash / FinalStateHash / EvidenceHash；
- MetricsJson；
- S0～S10 StageEvidenceJson。

Recovery 必须检查：

- Definition hash/head/evidence 一致；
- Result zero-control；
- Result EvidenceHash 可重算；
- Run Evidence 行数与 Result.Runs 一致；
- 所有 Hash 均为 SHA-256；
- DeterminismRound 在固定范围内。

## 5. Host / Desktop 边界

Host Controller 仅允许 `[HttpGet]` Evidence 查询，不提供 Execute/Apply/Replace/Dispatch/PLC 写入 API。

Desktop 页面只有：

- 刷新状态；
- 查看实验列表；
- 按 ExperimentId 读取 Result；
- 查看 Ranking/Pareto/EvidenceHash。

不存在策略应用、自动替换生产策略或设备控制动作。

## 6. exact-head 最终证据

以下字段只有在最终 Acceptance Head 冻结后写入，不使用中间 head 或历史 P5 artifact 冒充最终证据：

```text
Acceptance Head:        PENDING FINAL EXACT-HEAD GATE
Specialty Run/Artifact: PENDING
Stress Run/Artifact:    PENDING
Full Regression:        PENDING
One Hour Soak:          PENDING
PR expected==actual:    PENDING
```

最终必须验证所有 Artifact：

- `expired=false`；
- artifact name 与 run number 对应；
- Digest/SHA-256 与下载内容一致；
- evidence 中 `head_sha == Acceptance Head`；
- Full Regression 每个 child `completed/success` 且 `headSha == Acceptance Head`。

## 7. 安全结论模板

在所有最终门禁完成前，本报告不得宣称 P5 完成。完成后允许的结论仅为：

> IDI-P5 Digital Twin Optimizer 已在 exact software head 上完成推荐型数字孪生优化、确定性多策略对比、Hard Constraint、Pareto、多目标与 SQL Evidence 软件验收；仍不允许自动替换生产调度策略，不产生生产控制写入，不替代真实 HIL、机械安全或现场验收。
