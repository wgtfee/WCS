# IDI-P4 Maintenance Learning 操作手册

## 1. 使用边界

P4 用于维修学习和 Evidence，不是设备控制页面。Production 未批准环境保持 fail-closed；软件侧最高 L1。

固定安全值：

```text
ControlWriteAllowed=false
AutoTrainingAllowed=false
AutoModelActivationAllowed=false
ProductionAutomationAllowed=false
```

## 2. 数据流

```text
Health / RootCause / Forecast / Maintenance Proposal
    -> MaintenanceIntervention
    -> MES Outbox / Work Order
    -> MaintenanceOutcome callback
    -> Before/After FeatureSnapshot reference
    -> Versioned Evaluation Window
    -> MaintenanceEffectiveness
    -> TrainingLabelCandidate
    -> Human Approval
    -> Governed Dataset admission
```

未批准 Label 不得进入 Dataset；P4 不自动触发训练或 ModelOps Champion 切换。

## 3. Host 检查

在允许的 IndustrialIntelligence 环境下：

- `GET /api/maintenance-learning/status`：查看环境、L1、恢复计数和安全值；
- `GET /api/maintenance-learning/interventions/{id}`：查询维修记录；
- `GET /api/maintenance-learning/outbox/pending?limit=100`：查询待投递 MES Outbox；
- `POST /api/maintenance-learning/interventions`：写学习域 Intervention 元数据；
- `POST /api/maintenance-learning/outcomes`：写 MES/现场 Outcome，`SourceEventId` 幂等；
- `POST /api/maintenance-learning/labels`：创建 Pending Label；
- `POST /api/maintenance-learning/labels/{id}/decision`：人工批准或拒绝 Label。

这些 POST 不产生 PLC/CommandBus/Task/Dispatch/Traffic 控制消息。

## 4. SQL 与重启恢复

首次访问 P4 Host 时 `MaintenanceLearningPersistenceFactory` 初始化 P4 独立表和唯一索引。重启后 `IMaintenanceLearningRecovery` 读取：

- InterventionCount；
- PendingOutboxCount；
- PendingLabelCount；
- StateHash。

SQL 不可用时 P4 API 返回 503/fail-closed；WCS 确定性控制链路继续独立运行。

## 5. MES Outbox

- 每个请求必须使用稳定 `IdempotencyKey`；
- 重复 Enqueue 返回同一个逻辑请求；
- `AttemptCount` 有上限；
- Delivered 后不再出现在 Pending；
- 网络/MES 失败记录 LastError，不能无限重试。

## 6. Desktop

菜单 `IDI-P4 Maintenance Learning` 提供：

- Environment / Mode / Recovery；
- Pending MES Outbox；
- InterventionId 只读查询；
- 安全边界提示。

Desktop 不提供自动训练、自动模型激活或设备控制按钮。

## 7. CI

专项：`WCS IDI P4 Maintenance Learning Contract`。

最终累计：`WCS IDI P4 Full Regression` = exactly 51 child，包含 One Hour Soak。

任何代码或文档移动 P4 final Head 后，都必须重新运行最终 Specialty 和 51-child Full Regression，并重新核实 Artifact/Digest/head_sha。

## 8. 排错

- `404`：当前 Environment 未被 P0 Guard 允许，或 Production fail-closed；
- `503`：P4 SQL persistence 不可用，保持 fail-closed；
- Label 不能进入 Dataset：确认 State 必须为 `Approved`；
- Outcome 重复：检查 `SourceEventId`，重复回调应幂等；
- Censored：LongWindow 尚未完成，不得提前构造训练标签事实；
- Outbox retry bound：修复 MES/网络问题后由显式运维流程处理，不提高无限重试上限。
