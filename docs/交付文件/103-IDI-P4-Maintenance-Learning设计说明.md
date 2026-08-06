# IDI-P4 Maintenance Learning 设计说明

## 1. 阶段定位

IDI-P4 在已完成的 P0～P3 基线上建立维修结果学习闭环，但保持 **learning/evidence only**：不自动训练、不自动激活模型、不写 PLC、不改变任务、车辆、路权、Dispatch 或 Traffic 控制状态。

基线：`develop@87063c3929d6dd5a23274221889d390b9053a633`。

## 2. 核心对象

- `MaintenanceIntervention`：维修动作、资产、前后 FeatureSnapshot、成本、Actor、CorrelationId。
- `MaintenanceOutcome`：实际故障/无故障、停机、成本、MES SourceEventId。
- `VersionedEvaluationWindow`：按 AssetType 版本化 Immediate/Short/Medium/Long 观察窗口，并记录批准人和确定性 DefinitionHash。
- `MaintenanceEffectiveness`：停机差异、成本差异、FailureAvoided 和 EvidenceHash。
- `CausalCandidate` / `CounterfactualEstimate`：始终是 Estimated Evidence，不替代人工事实。
- `TrainingLabelCandidate` / `TrainingLabelApproval`：标签默认 Pending，仅显式批准后可进入 Dataset。
- `MesOutboxEntry`：MES 交付使用幂等键、重试次数上限和可恢复状态。

## 3. 领域治理

`MaintenanceLearningJournal` 负责：

- Intervention 幂等登记与有界数量；
- Outcome SourceEventId 幂等；
- 维修完成前 Outcome 拒绝；
- 观察窗口不足返回 `Censored`，不伪造正负结果；
- 完整观察窗口才生成 Effectiveness；
- TrainingLabel 显式审批、Actor/Reason/CorrelationId/IdempotencyKey 审计；
- 未批准标签不进入 Dataset。

`MaintenanceLearningWorkflow` 把 Intervention → MES Outbox → Outcome → Evaluation → Label Candidate → Human Approval 组合为一个受治理闭环，并始终返回 `ControlWriteAllowed=false`、`AutoTrainingAllowed=false`、`AutoModelActivationAllowed=false`。

## 4. SQL 持久化与恢复

Infrastructure 新增：

- `Wcs_MaintenanceIntervention`
- `Wcs_MaintenanceLearningOutcome`
- `Wcs_MaintenanceEffectiveness`
- `Wcs_MaintenanceEvaluationWindow`
- `Wcs_MaintenanceCausalCandidate`
- `Wcs_MaintenanceCounterfactualEstimate`
- `Wcs_MaintenanceTrainingLabel`
- `Wcs_MaintenanceTrainingLabelApproval`
- `Wcs_MaintenanceMesOutbox`

所有关键业务键和幂等键均建立唯一索引。`SqlMaintenanceLearningStore` 使用 SQL Server 事务实现 Intervention、MES Outcome、Label Approval 和 Outbox 的幂等写入，并通过 `IMaintenanceLearningRecovery` 恢复 Intervention/Pending Outbox/Pending Label 计数与确定性 StateHash。

SQL 不进入 `Wcs.MaintenanceLearning` 领域项目；SQL 不可用时 P4 API fail-closed，不能阻塞 WCS 控制线程。

## 5. Host API

`/api/maintenance-learning` 受 P0 `IndustrialIntelligenceEnvironmentGuard` 和 `MaximumAutomationLevel<=L1` 限制。

当前接口：

- `GET /status`
- `GET /interventions/{id}`
- `GET /outbox/pending?limit=`
- `POST /interventions`
- `POST /outcomes`
- `POST /labels`
- `POST /labels/{id}/decision`

POST 仅写维修学习/治理元数据，不产生 CommandBus、PLC、调度或交通控制命令。

## 6. Desktop

新增 `IDI-P4 Maintenance Learning` 页面，显示环境、Mode/L1、安全边界、恢复计数、Pending MES Outbox，并支持只读 Intervention 查询。页面不提供 PLC、设备、任务、路线、路权或自动训练/激活按钮。

## 7. 软件安全边界

P4 固定：

```text
MaximumAutomationLevel <= L1
ControlWriteAllowed = false
AutoTrainingAllowed = false
AutoModelActivationAllowed = false
ProductionAutomationAllowed = false
```

禁止 P4 领域依赖 `IPlcConnection`、S7/Snap7、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、UnifiedTransportDispatchEngine、TransportTrafficCoordinator 或 RouteReservation mutation。

## 8. 完成定义

最终 exact Head 必须同时满足：

- P4 Specialty 最终冻结测试数全绿；
- Host/Infrastructure/Desktop Release build 全绿；
- SQL 持久化/恢复和 MES 幂等/Outbox 真实代码通过门禁；
- 至少一个闭环样例证明 Intervention→Outcome→Effectiveness→Approved Label；
- P4 Full Regression = P3 的 50 个正式 child + P4 Specialty = exactly 51 child；
- One Hour Soak 在 51 child 内；
- `workflowCount=51`、`allSuccess=true`、每个 child exact Head；
- Specialty / Full Regression Artifact ID、名称、`expired=false`、SHA-256 Digest 和 head_sha 核实；
- PR Head 未漂移，Ready 后 squash merge 到 `develop`。
