# IDI-P3 Shadow Decision 设计说明

## 1. 目标

IDI-P3 在 P0 Governance、P1 ModelOps 与 P2 Feature Center 基础上建立只读决策建议层。P3 只生成、治理、审批和回填 `DecisionProposal`，不进入 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic 或 RouteReservation 写链路。

## 2. 标准对象

P3 使用标准 `DecisionProposal`、`Explanation`、`ConstraintResult`、`Approval` 与 `Outcome` 契约。第一批 ProposalType：

- MaintenanceWindowRecommendation
- AssetLoadReductionRecommendation
- VehicleSelectionRecommendation
- TaskPriorityRecommendation
- StandbyAssetRecommendation
- InspectionRecommendation

车辆选择和任务优先级仅记录建议，不修改正式调度结果。

## 3. 执行顺序

`Read-only runtime snapshot -> FeatureSnapshot -> Champion/Shadow inference -> Candidate -> Hard Constraint -> Impact -> Explanation -> Proposal Journal -> Optional Approval -> External Result -> Outcome`

无 FeatureSnapshot 时不生成 Proposal；无可用 Champion 时返回 `ModelUnavailable`；Hard Constraint 可阻断候选并持久化阻断原因。Explanation 必须绑定模型、FeatureSnapshot、规则/约束 Evidence，保证同输入和版本可重放。

## 4. SQL

P3 使用五张独立治理表：

- `Wcs_DecisionProposal`
- `Wcs_DecisionConstraintResult`
- `Wcs_DecisionApprovalJournal`
- `Wcs_DecisionOutcomeJournal`
- `Wcs_DecisionExplanationEvidence`

SQL 持久化属于异步治理路径；SQL/MES 不可用不得阻塞确定性 WCS 控制线程。

## 5. API 与 Desktop

正式 Host 表面限定为 `/api/industrial-intelligence/proposals`：列表、详情、approve、reject、outcome。审批只改变 Proposal 治理状态，禁止产生 CommandBus 控制消息。Desktop Shadow Decision Center 只展示 Proposal、约束、Explanation、审批审计和 Outcome，并提供治理动作，不提供设备控制动作。

## 6. 有界与安全边界

Proposal 队列、保留期、查询范围必须有界；过期 Proposal 不可批准。P3 保持 `MaximumAutomationLevel<=L1`、`ControlWriteAllowed=false`、`ProductionAutomationAllowed=false`。Feature/Model/Decision/SQL/MES 故障不得影响 PLC Polling、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic。

## 7. 完成定义

同一未漂移 exact Head 必须同时满足：P3 Specialty 精确测试全绿；累计 Full Regression exactly 50/50 completed/success 且包含 One Hour Soak；P0/P1/P2 与 Production fail-closed、zero-control 继续通过；Host/Desktop/SQL/重启恢复和文档完成；Specialty/Full Regression Artifact 的 ID、名称、expired=false、SHA-256 Digest、head_sha 核实；PR Ready 后使用 expected-head squash merge 到 develop。