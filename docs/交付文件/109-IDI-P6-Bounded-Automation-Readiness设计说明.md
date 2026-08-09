# IDI-P6 Bounded Automation Readiness 设计说明

## 1. 阶段目标

IDI-P6 是 WCS Industrial Decision Intelligence v4.0 的最终软件治理阶段。它建立“受边界约束的自动化就绪评估”，但**不授予生产控制权限**。本阶段最终声明固定为：

> `software-side ready only`

P6 不改变 P0 已存在的 Production fail-closed 规则，不放宽 `IndustrialIntelligenceEnvironmentGuard` 的 L0/L1 运行边界，也不修改 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic、RouteReservation 等生产控制事实源。

## 2. 核心治理对象

P6 固化九类治理输入，默认全部 Disabled / fail-closed：

1. `AutomationPolicy`：声明候选自动化等级、版本和不可变 PolicyHash；
2. `ExecutionAllowance`：仅允许 `SoftwareSimulation` / `ShadowObservation` 软件侧评估，不包含生产执行类型；
3. `RateLimit`：无有效上限即拒绝；
4. `BudgetLimit`：无有效预算即拒绝；
5. `MaintenanceWindow`：必须为合法 UTC 时间窗口；
6. `ApprovalRequirement`：必须配置审批数量并要求独立安全审批；
7. `CircuitBreaker`：必须配置故障阈值和打开时间；
8. `KillSwitch`：必须 Enabled + Armed 才能满足软件侧 readiness；
9. `RollbackPolicy`：必须具备目标版本和最大回滚时间。

这些对象只参与确定性 readiness 判定，不接入生产控制写链路。

## 3. 自动化等级与 Evidence 规则

### 3.1 L0/L1

满足全部治理条件、合法 Git Software Head、合法 SHA-256 Evidence 后，可得到软件侧 readiness。返回值仍包含：

- `ProductionEnablementAllowed=false`；
- `Claim=software-side ready only`。

### 3.2 L2/L3

L2/L3 仅表示 P6 可以评估更高等级的**软件侧准备条件**。必须同时具备：

- real Site Evidence；
- real HIL Evidence；
- independent Safety Approval Evidence；
- verified Rollback Evidence。

缺少任一 Evidence 都 fail-closed。即使四类 Evidence 全部存在，P6 仍只可返回 `SoftwareSideReady=true`，**不能把 ProductionEnablementAllowed 改为 true**。

### 3.3 L4

P6 不评估 L4；请求 L4 直接拒绝。

## 4. 永久禁止自动化范围

P6 将以下 11 类动作固化为 permanent prohibition，任何候选请求包含其中一项都会拒绝：

- EmergencyStop；
- SafetyReset；
- SafetyDoorBypass；
- LightCurtainBypass；
- MechanicalInterlockBypass；
- PlcForceWrite；
- AutomaticRoadRightRelease；
- AutomaticBlockRelease；
- UnapprovedShutdown；
- StateMachineBypass；
- TrafficConstraintBypass。

该清单不能被普通配置、Policy、Evidence 或 UI 绕过。若请求集合出现未定义的 enum/未知 operation 值，同样按 fail-closed 拒绝，不能因为“不在 11 个已知名称中”而忽略。

## 5. 确定性 Evaluator

`BoundedAutomationReadinessEvaluator` 是无副作用纯判断逻辑。输入包括九类治理对象、环境、Evidence 和请求的 prohibited operation；输出包括：

- `SoftwareSideReady`；
- `ProductionEnablementAllowed`；
- `EffectiveMaximumAutomationLevel`；
- `Claim`；
- `Reasons`。

所有拒绝原因显式记录，便于审计与 Evidence 追踪。Git Software Head 与业务 Evidence Hash 分开校验：当前 Git SHA-1 40 位和未来兼容的 64 位 Git commit SHA 都可作为 Software Head，业务 Evidence/Policy/Decision 使用 SHA-256。未知 ExecutionAllowance 或未知 automation operation 数值都必须产生显式拒绝原因。

## 6. Evidence 模型

`BoundedAutomationReadinessEvidenceRecord` 是不可变验收记录，包含：

- EvaluationId；
- EvaluatedAtUtc；
- EnvironmentName；
- RequestedLevel；
- PolicyVersion / PolicyHash；
- SoftwareHeadSha；
- SourceEvidenceHash；
- DecisionHash；
- SoftwareSideReady；
- ProductionEnablementAllowed（P6 必须为 false）；
- Claim；
- Reasons。

`BoundedAutomationReadinessEvidenceHash` 对治理输入和判定输出构造 canonical text 后计算 SHA-256，保证相同输入得到稳定 DecisionHash。

## 7. SQL Evidence 持久化

表：`Wcs_BoundedAutomationReadinessEvidence`。

设计原则：

- append-only；
- EvaluationId 唯一；
- 相同 EvaluationId + 相同 DecisionHash 可幂等重放；
- 相同 EvaluationId + 不同 DecisionHash 拒绝；
- 不提供 Update/Delete 业务路径；
- 读取时重新验证 Git SHA、SHA-256、software-only claim 和 Production=false；
- 列表查询上限 500。

该表是 Evidence，不是“自动化执行任务表”。

## 8. Host API 边界

`BoundedAutomationReadinessController` 只有四个 GET：

- `GET /api/bounded-automation-readiness/status`；
- `GET /api/bounded-automation-readiness/prohibitions`；
- `GET /api/bounded-automation-readiness/evidence`；
- `GET /api/bounded-automation-readiness/evidence/{evaluationId}`。

不存在 POST/PUT/PATCH/DELETE；不存在 enable、approve、execute、kill-switch operation、rollback execution、PLC write 或 dispatch control endpoint。

所有 GET 仍先通过 P0 `IndustrialIntelligenceEnvironmentGuard`。Production 环境继续 fail-closed。

## 9. Desktop 边界

Desktop 页面 `IDI-P6 Bounded Automation Readiness` 仅展示：

- Environment / Mode / HostMax；
- `software-side ready only`；
- Production=false / ControlWrite=false；
- 11 类 permanent prohibitions；
- 最近 Evidence；
- 指定 EvaluationId 的 Evidence 查询。

页面没有启用、审批、执行、解除、复位或回滚按钮。

## 10. 测试与累计验收设计

P6 软件验收由四层构成：

1. Specialty：43 个 governance tests + 12 个 Evidence/API tests = 55；
2. Stress/Soak：6 个高循环/并发契约 × 3 轮 = 18，并额外重跑完整 55；
3. SQL Evidence：真实 SQL Server 2022 service container，固定 6 个集成测试；
4. Cumulative Full Regression：继承 P5 全部 53 child，并追加 P6 Specialty、P6 Stress、P6 SQL，合计 exactly 56 child，其中继续包含 One Hour Soak。

真实 HIL 不属于软件矩阵的伪造 gate。P6 若无真实 Site/HIL/Safety/Rollback Evidence，只能保持 fail-closed。

## 11. 最终验收声明

P6 软件验收完成后允许声明：

> `software-side ready only`

不得声明“Production automation enabled”“现场 L2/L3 已验收”“真实 HIL 已通过”，除非未来存在独立的现场流程、真实硬件 Evidence、安全审批和专门的生产授权阶段。P6 本身没有这种授权能力。
