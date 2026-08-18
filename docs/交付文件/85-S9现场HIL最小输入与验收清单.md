# S9 现场 HIL 最小输入与验收清单

## 1. 目的

本清单把 S9 最终阻塞项压缩为现场必须提供的最小真实输入。仓库软件侧全部完成后，只要以下真实条件没有满足，S9 就保持 `Real HIL Pending`。

## 2. 必须存在的台架信息

| 项目 | 必填 | 要求 |
|---|---|---|
| BenchId | 是 | 与批准 Hardware Profile 完全一致 |
| Topology Revision | 是 | 能追溯 PLC/RGV/传感器/互锁台架版本 |
| Controller AssetIds | 是 | Trial Plan 引用的 PLC 必须在名单中 |
| Vehicle AssetIds | 按计划 | 涉及 RGV 动作时必须存在 |
| PLC Protocol | 是 | 与现场实际协议一致 |
| ProductionNetworkIsolated | 是 | 必须 true |
| UsesProductionCredentials | 是 | 必须 false |
| ApprovedBy / ApprovedAt | 是 | 台架档案批准记录 |

仓库不要求提交真实 IP、密码、Token、生产连接串；这些由现场受保护配置管理。

## 3. 必须存在的变更与人员信息

```text
ChangeTicket
MaintenanceWindowId
Operator
SafetyApprover
ProcedureRevision
```

Operator 与 SafetyApprover 必须是不同人员。

## 4. Preflight 必须全部为 true

```text
EmergencyStopVerified
MechanicalInterlocksVerified
GuardingVerified
NetworkIsolationVerified
MaintenanceModeVerified
OperatorAreaClear
```

任一 false 都不得 BeginExecution。

## 5. Self-hosted Runner 最小要求

Runner Labels：

```text
self-hosted
wcs-hil
```

执行声明：

```text
RunnerKind=SelfHostedHil
RealHardwareConnected=true
ProductionNetworkIsolated=true
UsesProductionCredentials=false
BenchId=<approved BenchId>
SoftwareHead=<exact approved 40-char SHA>
```

必须配置受保护的 `wcs-hil` GitHub Environment，并在 Runner 本地准备：

```text
WCS_HIL_EVIDENCE_MANIFEST=<local manifest path>
WCS_HIL_EVIDENCE_BUNDLE=<local evidence bundle path>
```

## 6. 每个 Trial Step 的最小证据

```text
StepId
AssetId
Result=Passed
RealHardwareObserved=true
EvidenceSha256=<64 hex>
OccurredAtUtc
```

任何 Step 出现 Failed，当前 Session 都不能 Completed；不得靠补一条 Passed 覆盖失败。

## 7. Abort 后最小恢复证据

如果发生 Abort：

```text
MotionStopped=true
PlcOutputsSafe=true
MechanicalInterlocksRestored=true
EmergencyStopStateVerified=true
OperatorAreaClear=true
RealHardwareObserved=true   # 已进入真实 HIL 时必须
EvidenceBundleSha256=<64 hex>
```

恢复后旧 Session 进入 `Recovered` 终态；若重试必须创建新 Session 并填写 `RecoveryFromSessionId`。

## 8. 最终 Acceptance 最小证据

```text
ProtocolValidated=true
MechanicalSafetyAccepted=true
SiteAccepted=true
AcceptedBy=<real site acceptor>
ProtocolEvidenceSha256=<64 hex>
MechanicalSafetyEvidenceSha256=<64 hex>
SiteAcceptanceEvidenceSha256=<64 hex>
EvidenceBundleSha256=<64 hex>
```

以上三项 Acceptance 不允许由 GitHub hosted CI、Simulation、Mock 或仓库代码自动置 true。

## 9. Evidence Manifest

Schema：

```text
wcs-s9-hil-evidence/v1
```

Manifest 必须绑定：

- exact Software Head；
- SessionId；
- BenchId；
- Runner identity；
- Preflight；
- 全部 Step；
- Acceptance；
- Evidence Bundle SHA-256。

`validate_hil_evidence.py` 会重新计算二进制 Bundle 的 SHA-256，Manifest 声明与文件实际 Hash 不一致即 fail-closed。

## 10. S9 可以完成的唯一判定

只有以下全部为真才允许写“S9 完成”：

```text
26/26 Governance success
8/8 Software Readiness success
45/45 exact-head Software Full Regression success
WCS S9 Real HIL Evidence Gate success on [self-hosted,wcs-hil]
RealHardwareConnected=true
ProtocolValidated=true
MechanicalSafetyAccepted=true
SiteAccepted=true
PR Head exact and unchanged
```

在真实 HIL Gate 未成功前，唯一正确状态是：

```text
S9 software-side ready; real HIL / site acceptance pending.
```
