# IDI-P6 Bounded Automation Readiness 专项测试报告

## 1. 报告范围

本报告定义 IDI-P6 软件侧正式验收范围。最终事实源为 GitHub PR #50 的冻结 Acceptance Head、对应 Actions Runs、Artifacts 与 SHA-256 Digest；文档不预写最终 Run/Artifact 编号，避免为了更新编号再次移动 Acceptance Head。

P6 的最终声明只能是：

> `software-side ready only`

真实 Site、真实 HIL、机械/电气安全验收、Production L2/L3 授权均不由本软件测试报告替代。

## 2. Specialty Contract Gate

Workflow：`.github/workflows/idi-p6-bounded-automation-readiness.yml`

固定测试数量：54。

### 2.1 Governance：42/42

覆盖：

- 九类治理对象默认 Disabled；
- PolicyVersion / PolicyHash；
- ExecutionAllowance；
- RateLimit / BudgetLimit；
- MaintenanceWindow；
- ApprovalRequirement；
- CircuitBreaker；
- KillSwitch；
- RollbackPolicy；
- Software Evidence / Git commit SHA / EvidenceHash；
- L1 software-side readiness；
- L2/L3 Site/HIL/Safety/Rollback Evidence 要求；
- Evidence 齐全后 Production 仍然 false；
- 11 类 permanent prohibition；
- L4 拒绝；
- P0 Production fail-closed 与 L1 边界未被削弱。

### 2.2 Evidence / API：12/12

覆盖：

- DecisionHash 确定性；
- Policy 变化导致 Evidence Hash 变化；
- EvidenceRecord governed hash；
- 非法 EvaluationId；
- Production=true Evidence 拒绝；
- append-only 内存 store 幂等；
- 冲突 EvaluationId 拒绝；
- bounded list；
- Host Controller 精确四个 GET；
- status 明确 software-only / Production=false / ControlWrite=false；
- 11 类 permanent prohibition 可只读查询；
- Production 环境 Host 访问继续 fail-closed。

### 2.3 编译与静态边界

同一 gate 还必须：

- Release build `Wcs.Simulator.Tests`；
- Release build `Wcs.Desktop`，验证 P6 XAML / DI / API client；
- P6 Governance 不含 S7/Snap7/PLC Client/CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic/RouteReservation 控制依赖；
- Host 不存在 POST/PUT/PATCH/DELETE；
- SQL Evidence 不存在 P6 Updateable/Deleteable 路径；
- `ProductionEnablementAllowed=false`；
- `controlWriteAllowed=false`；
- final claim 为 `software-side ready only`。

## 3. Stress / Soak Gate

Workflow：`.github/workflows/idi-p6-automation-readiness-stress-soak.yml`

固定 Stress Contract：6 个，每个 workflow 执行 3 轮，预期 18/18：

1. 10,000 次相同请求决策及 DecisionHash 完全确定；
2. 20,000 次并行 L2 evaluator 不得出现 Production=true；
3. 5,000 条并行 append-only Evidence 写入后可查询且保持不可变；
4. 11 类 permanent prohibition 每类重复 1,000 次仍拒绝；
5. L2 缺真实 Evidence 重复 5,000 次持续 fail-closed；
6. L3 Evidence 齐全重复 5,000 次仍只 software-side ready。

Stress gate 之后再次完整执行 42 + 12 = 54 个 Specialty Contract，防止压力测试与专项契约脱节。

## 4. SQL Evidence Gate

Workflow：`.github/workflows/idi-p6-readiness-sql.yml`

环境：GitHub Actions SQL Server 2022 Linux service container。

固定测试：6/6：

1. CodeFirst schema + Append/Get round-trip；
2. 相同 immutable record 重放幂等；
3. 相同 EvaluationId、不同 DecisionHash 拒绝；
4. List 上限、排序和 501 拒绝；
5. L3 software-ready Evidence SQL round-trip 后 Production 仍 false；
6. `ProductionEnablementAllowed=true` 的非法记录在 insert 前拒绝。

SQL 静态检查同时要求：

- 表名为 `Wcs_BoundedAutomationReadinessEvidence`；
- EvaluationId 不可变；
- P6 persistence 无 Update/Delete；
- Production=false。

## 5. Cumulative Full Regression

Workflow：`.github/workflows/idi-p6-full-regression.yml`

固定 child 数量：exactly 56。

组成：

- P5 已验收的 53 个 child 原样继承；
- P6 Bounded Automation Readiness Contract；
- P6 Automation Readiness Stress Soak；
- P6 Readiness SQL Evidence。

因此 P6 没有删除、跳过或替换 P5 的任何 child。矩阵仍包含：

- Windows CI；
- End-to-End Load；
- One Hour Soak Load；
- PLC anomaly / telemetry / model adapter；
- Asset health / maintenance / forecast；
- Simulation S0～S10 软件门禁；
- IDI-P0～P5 Specialty；
- P5 Stress/Soak；
- P6 三个新增 gate。

Full Regression 必须验证每个 child：

- `head_sha == Acceptance Head`；
- status completed；
- conclusion success；
- PR Head 未漂移。

## 6. One Hour Soak

P6 不降低 P5 已有 One Hour Soak 门禁。最终 56-child Acceptance Head 必须存在相同 exact-head 的 `WCS One Hour Soak Load` success。不能用 S8 虚拟 8h/24h 测试替代真实一小时累计门禁。

## 7. Artifact / Digest 验证

正式收口时至少核对：

- P6 Specialty Artifact；
- P6 Stress Artifact；
- P6 SQL Artifact；
- P6 Full Regression Artifact；
- One Hour Soak Artifact。

每个 Artifact 要求：

- expired=false；
- workflow_run.head_sha 与冻结 Acceptance Head 一致；
- GitHub `sha256:` digest 与下载 ZIP 本地 SHA-256 一致；
- Evidence JSON 中 head_sha / workflowCount / testCount / allSuccess 等字段符合 gate 定义。

最终 Artifact ID、Run ID 和 Digest 记录在 PR #50 Conversation / GitHub Actions 中作为事实源，不写入本文件后再产生额外文档 commit。

## 8. 安全验收结论模板

只有全部软件门禁通过后才允许使用以下结论：

```text
IDI-P6 software-side acceptance: PASS
Final claim: software-side ready only
ProductionEnablementAllowed: false
ControlWriteAllowed: false
Permanent prohibitions: 11/11 retained
Real HIL gate included: false
```

不得把以上结论解释成生产自动化授权或现场 HIL 通过。
