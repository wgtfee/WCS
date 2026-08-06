# IDI-P4 Maintenance Learning 专项测试报告

## 1. 测试范围

P4 Specialty workflow：`WCS IDI P4 Maintenance Learning Contract`。

当前冻结候选测试数为 24，覆盖：

- zero-control / no-auto-training / no-auto-activation；
- EvaluationWindow Hash 与严格递增窗口；
- Intervention 幂等、Snapshot 必填和容量上限；
- Outcome 关联、时间顺序和 MES SourceEventId 幂等；
- Pending/Censored/Complete 观察状态；
- FailureObserved 对 Effectiveness 的影响；
- TrainingLabel Pending/Approval/Dataset admission；
- Label decision 幂等和 Audit；
- MES Outbox 幂等、容量、重试和 delivered 清理；
- `MaintenanceLearningWorkflow` 持久化调用；
- 闭环样例 Intervention→Outcome→Evaluation→Approved Label；
- Infrastructure / Host / Desktop Release build；
- SQL 持久化、恢复、Host L1 与 Desktop zero-control 静态边界。

## 2. 累计回归

P4 Full Regression 固定为：

```text
P3 formal software matrix: 50
P4 Specialty:              +1
Total:                     51
```

必须包含 `WCS One Hour Soak Load`，且最终 Evidence 要求：

```text
workflowCount = 51
allSuccess = true
all child status = completed
all child conclusion = success
all child headSha = final exact Head
ControlWriteAllowed = false
AutoTrainingAllowed = false
AutoModelActivationAllowed = false
ProductionAutomationAllowed = false
MaximumAutomationLevel = L1
```

## 3. 禁止的验收捷径

不得删除或排除失败测试，不得减少 51 child，不得复用其他 Head 的成功 run，不得排除 One Hour Soak，不得把 SQL/MES 故障改成控制线程阻塞，不得放宽 Production fail-closed 或 zero-control。

## 4. 最终 Evidence 填写规则

只有最终 exact Head 绿灯后填写并核实：

- Specialty Run / Artifact ID / Artifact name / `expired=false` / SHA-256 Digest；
- Full Regression Run / Artifact ID / Artifact name / `expired=false` / SHA-256 Digest；
- PR expected Head == actual Head；
- 51/51 exact-head；
- Ready + squash merge commit。

在最终证据产生之前，本报告不预填成功结论，避免使用旧 Head Evidence 冒充最终验收。
