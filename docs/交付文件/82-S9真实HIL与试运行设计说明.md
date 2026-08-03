# S9 真实 HIL 与试运行设计说明

## 1. 阶段定位

S9 是 WCS Simulation & Verification v1.0 从“软件侧仿真验证”进入“真实硬件在环与受控试运行”的边界阶段。

S8 已证明仓库级软件具备进入 S9 的软件条件，但 S8 的虚拟 8h/24h、容量、恢复和 43/43 exact-head 证据不能替代真实 PLC、真实 RGV、工业网络、机械互锁和现场验收。S9 只在存在真实硬件台架、维护窗口、安全审批和现场证据时才允许形成 `RealHilExecuted=true`。

S9 固定引用 S8 最终 Evidence Head：

```text
02b202862816a91ff473925bb964e4d2aa2f6470
```

## 2. 模块边界

```text
Wcs.Simulator/HilVerification
├── HilVerificationContracts.cs
├── HilVerificationRuntime.cs
└── HilEnvironmentBoundaryGuard.cs

Wcs.Host/Controllers
└── HilVerificationController.cs       # 只读 inspection/status

scripts/s9
└── validate_hil_evidence.py           # 只校验现场产生的证据，不连设备
```

`HilVerificationRuntime` 是 HIL 治理与证据状态机，不是硬件驱动。它不打开 Socket/HTTP/SQL/S7/Snap7 连接，不调用生产 CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic 或真实模型推理。

真实硬件动作只能由现场所有的 HIL Runner/适配层执行，并将现场产生的 Evidence Manifest 与二进制证据包交给仓库提供的校验器验真。

## 3. 会话状态机

正常链路：

```text
Defined
  ↓ Safety Preflight
PreflightPassed
  ↓ Arm
Armed
  ↓ SelfHostedHil + real hardware attestation
Running
  ↓ every planned step has passing real-hardware evidence
Completed
  ↓ protocol + mechanical safety + site acceptance
Accepted
```

异常链路：

```text
Defined / PreflightPassed / Armed / Running
  ↓ Abort
Aborted
  ↓ physical safe-state recovery verification
Recovered    # 终态
```

`Recovered` 会话不能重新进入 `Running`。若需要重试，必须创建新的 Session，并通过 `RecoveryFromSessionId` 显式引用已恢复的旧 Session，形成可审计链路。

安全预检失败进入 `Rejected`，也不能通过普通重试绕过。

## 4. Hardware Profile 与计划绑定

`HilHardwareProfileDefinition` 只记录受控 Bench 身份、协议、Topology Revision 和逻辑 AssetId，不在仓库保存真实 IP、密码、Token、连接串或生产凭据。

硬性要求：

- `ProductionNetworkIsolated=true`；
- `UsesProductionCredentials=false`；
- Hardware Profile 必须有批准人和批准时间；
- 至少存在一个 Controller Asset；
- PLC/RGV AssetId 在档案内唯一；
- Trial Plan 中每个 Step 的 AssetId 必须属于该 Hardware Profile；
- 真正 endpoint/credential 由现场 HIL Runner 的受保护配置管理。

## 5. 安全预检与双人审批

进入 `Armed` 前必须显式确认：

- 急停已验证；
- 机械互锁已验证；
- 防护/围栏已验证；
- HIL 网络与生产网络隔离；
- 设备处于批准的维护/试运行模式；
- 人员区域清空；
- Procedure Revision 明确；
- Operator 与 SafetyApprover 为不同人员，且与 Session Manifest 一致。

缺任意一项即 fail-closed。

## 6. 真实执行 Attestation

从 `Armed` 进入 `Running` 只接受：

```text
RunnerKind = SelfHostedHil
RunnerLabels contains self-hosted + wcs-hil
RealHardwareConnected = true
ProductionNetworkIsolated = true
UsesProductionCredentials = false
BenchId = approved HardwareProfile.BenchId
SoftwareHead = Session.SoftwareHead
EvidenceBundleSha256 = valid SHA-256
```

GitHub hosted runner、仿真 runner、Virtual PLC/RGV 结果均不能推进真实 HIL 会话。

## 7. Trial Plan 与 Evidence

支持的步骤类型包括：

- ConnectivityRead；
- PlcRead；
- ControlledPlcWrite；
- VehicleMove；
- InterlockVerify；
- EmergencyStopVerify；
- RecoveryVerify；
- ExternalAckVerify；
- ProtocolRoundTripVerify；
- SensorFeedbackVerify；
- ControlledStopVerify。

治理 Runtime 只记录契约和结果，不执行这些动作。

每条 `StepResult` 必须：

- 属于 Trial Plan 中已批准的 StepId；
- AssetId 与 Step 定义一致；
- `RealHardwareObserved=true`；
- 有严格递增 Sequence；
- 有合法 `EvidenceSha256`；
- 时间落在受控 Session Duration 内。

所有步骤必须最终有真实硬件 `Passed` 证据，且任何步骤一旦出现 `Failed` 证据，该 Session 不能被 `CompleteExecution` 伪装成通过。

## 8. Abort / Recovery

现场发生异常时必须 Abort，随后验证：

```text
MotionStopped=true
PlcOutputsSafe=true
MechanicalInterlocksRestored=true
EmergencyStopStateVerified=true
OperatorAreaClear=true
```

若该 Session 已经真实进入 HIL 执行，Recovery 还必须 `RealHardwareObserved=true`，并提供独立 Evidence Bundle SHA-256。

恢复完成只意味着设备回到经过验证的安全状态，不意味着原测试可以继续。任何重试都创建新 Session。

## 9. Evidence Bundle

仓库内使用两类 Hash：

1. `EvidenceHash`：对 Session Manifest、Hardware Profile、Trial Plan、Preflight、Execution、Step Evidence、Abort/Recovery/Acceptance 状态做规范化 SHA-256；
2. `EvidenceBundleSha256`：现场原始证据包的 SHA-256，由 self-hosted HIL Runner 外部产生并由 `validate_hil_evidence.py` 重新计算核对。

真实 Evidence Manifest Schema 固定为：

```text
wcs-s9-hil-evidence/v1
```

校验器必须核实 exact Software Head、BenchId、SessionId、Runner 身份、Preflight、每个 Step、协议/机械/现场验收以及二进制 Evidence Bundle 的实际 SHA-256。

## 10. Host Inspection API

S9 Host 只提供：

```text
GET /api/hil/verification/status
GET /api/hil/verification/acceptance-requirements
```

没有 POST/PUT/PATCH/DELETE 控制入口，不能 Arm、BeginExecution、Abort、Recover、Accept 或写 PLC。

仅允许 `HIL` / `TrialRun` 环境且 `HilVerification.Enabled=true`；Production 永远 404。`appsettings.HIL.json` 与 `appsettings.TrialRun.json` 同时设置 `Simulator.Enabled=true`，因此该 Host Profile 是只读 inspection 进程，不启动 Program.cs 的真实 PLC polling/control background services。

## 11. CI / HIL 门禁

### 11.1 WCS S9 HIL Governance Contract

- 26/26 固定契约测试；
- 状态机、Abort/Recovery、证据完整性、审批、容量边界；
- 静态禁止真实 IO 和生产控制依赖。

### 11.2 WCS S9 Software Trial Readiness

- 8/8 固定测试；
- Production 404 / HIL、TrialRun 只读可见；
- 配置 fail-closed；
- Controller 只有 GET；
- Real HIL workflow 必须是 manual + self-hosted。

### 11.3 WCS S9 Software Full Regression

- S8 的 43 条 exact-head 工作流；
- 加 `HIL Governance Contract`；
- 加 `Software Trial Readiness`；
- 共 45 条 exact-head software children；
- `workflowCount=45`；
- `allSuccess=true`；
- 每个 child `completed/success` 且 `headSha` 精确等于当前 S9 软件验收 Head；
- 包含 One Hour Soak。

该 45-child Matrix **明确不包含**真实 HIL Gate，不能据此宣告 S9 完成。

### 11.4 WCS S9 Real HIL Evidence Gate

- 仅 `workflow_dispatch`；
- `runs-on: [self-hosted, wcs-hil]`；
- 使用受保护 `wcs-hil` Environment；
- 要求现场 Runner 本地 Manifest/Bundle；
- Checkout 用户输入的 exact Software Head；
- 校验 Evidence Bundle 后上传 90 天 Artifact。

没有专用 self-hosted Runner 和现场真实证据时，该门禁不会被 hosted CI 自动跑成成功。

## 12. 最终验收语义

`Completed` 只说明真实 Bench 上 Trial Plan 的所有步骤已有通过证据，不等于 S9 Accepted。

进入 `Accepted` 仍必须：

```text
ProtocolValidated=true
MechanicalSafetyAccepted=true
SiteAccepted=true
ProtocolEvidenceSha256=<real evidence>
MechanicalSafetyEvidenceSha256=<real evidence>
SiteAcceptanceEvidenceSha256=<real evidence>
EvidenceBundleSha256=<final real bundle>
```

因此仓库 hosted CI 永远不能单独宣告 S9 完成。

## 13. S9 完成标准

只有同时满足以下条件才可将 PR #42 从 Draft 标记 Ready 并进入合并：

1. S9 软件侧 Governance 与 Trial Readiness 全绿；
2. 45/45 exact-head 软件全回归全绿；
3. 软件侧 Artifact/Digest 固化；
4. approved Hardware Profile / Trial Plan / Change Ticket / Maintenance Window 就绪；
5. `WCS S9 Real HIL Evidence Gate` 在专用 self-hosted runner 成功；
6. Manifest/Bundle 明确真实硬件执行并通过 Hash 校验；
7. ProtocolValidated、MechanicalSafetyAccepted、SiteAccepted 都有真实外部证据；
8. PR Conversation 固化最终 Evidence；
9. 最终 PR Head 未漂移，再进行受保护 Merge。

缺 4～7 任一项时，S9 状态只能写作“软件侧 Ready / Real HIL Pending”，不得写作 Completed。

## 14. 安全红线

- 不在 GitHub hosted runner 连接现场设备；
- 不在仓库保存真实生产凭据；
- 不绕过 PLC/设备已有安全联锁；
- 不把 Simulation、Mock、手工构造 JSON 当作真实 HIL；
- 不允许 Abort 后原 Session 直接恢复 Running；
- 不因测试失败而降低安全门槛；
- 未获得协议、机械安全和现场显式验收时，S9 不得标记完成。
