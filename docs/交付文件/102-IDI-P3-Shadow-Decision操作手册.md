# IDI-P3 Shadow Decision 操作手册

## 1. 使用边界

Shadow Decision Center 是建议治理界面，不是 WCS 控制台。任何 Proposal 的 approve/reject 只改变 Proposal 状态；P3 不发送 CommandBus，不写 PLC，不改变 TaskScheduler、TaskOrchestrator、DeviceManager、车辆选择、正式路线、Dispatch 或 Traffic 状态。

## 2. 建议处理流程

1. 确认 P0 Environment Guard 允许当前非生产治理环境，且最大自动化等级不高于 L1。
2. 检查 FeatureSnapshot、FeatureSchema 和 Model Champion 状态。
3. 查看 Proposal 列表及 `ProposalType`、状态、生成时间、过期时间和 Evidence。
4. 打开详情，核对 Explanation 中的模型、FeatureSnapshot、规则/约束 Evidence。
5. Hard Constraint 阻断的 Proposal 不得通过人工操作绕过。
6. 对仍有效的 Proposal 使用具名 Actor 执行 approve 或 reject，并填写 Reason/CorrelationId。
7. 外部实际动作完成后，通过 outcome 回填真实任务、维修、成本、停机或收益结果。
8. Outcome 只用于评价和后续学习，不反向直接修改控制状态。

## 3. 异常处理

- `FeatureSnapshotRequired`：先恢复 Feature Center 数据链路，不允许绕过快照生成建议。
- `ModelUnavailable`：恢复受治理 Champion；禁止临时使用未登记模型。
- Proposal Expired：创建新的评估，不允许批准过期建议。
- SQL 不可用：保持 WCS 控制运行；治理写入失败应显式失败/重试，不得阻塞控制线程。
- MES/Outcome 回调失败：保留关联标识并重试，不得伪造 Outcome。

## 4. 审计检查

审批/拒绝至少核对 Actor、Reason、CorrelationId、ProposalId、时间与 Evidence；Outcome 至少核对 ProposalId、外部对象关联、实际结果与时间。任何自动提升为控制动作均属于 P3 越界。

## 5. 发布前检查

最终发布必须确认：Production fail-closed；`MaximumAutomationLevel<=L1`；`ControlWriteAllowed=false`；`ProductionAutomationAllowed=false`；P3 Specialty 在最终 exact Head 全绿；累计 50-child Full Regression 全绿并包含 One Hour Soak；最终 Artifact ID/名称/expired=false/SHA-256/head_sha 已核实；PR Head 未漂移后才允许 Ready + squash merge。