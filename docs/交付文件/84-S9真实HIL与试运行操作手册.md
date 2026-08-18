# S9 真实 HIL 与试运行操作手册

## 1. 适用范围

本手册用于 S9 的软件侧准备、真实 HIL 台架执行、异常恢复、Evidence 固化与最终现场验收。它不授权绕过 PLC/设备安全联锁，也不提供远程控制接口。

## 2. 软件侧准备

软件侧必须先满足：

1. `WCS S9 HIL Governance Contract` 26/26；
2. `WCS S9 Software Trial Readiness` 8/8；
3. `WCS S9 Software Full Regression` 45/45 exact-head；
4. One Hour Soak success；
5. PR Head 与三个软件 Gate 的 Head 完全一致；
6. 软件 Artifact/Digest 已记录。

软件侧全绿只能进入“Ready for Real HIL”，不能标记 S9 Completed。

## 3. HIL Host inspection

仅在 `HIL` / `TrialRun` 环境使用：

```text
GET /api/hil/verification/status
GET /api/hil/verification/acceptance-requirements
```

该 Host Profile 使用 `Simulator.Enabled=true`，只用于 inspection/status，不启动真实 PLC polling/control background service。

Production 下上述 API 必须 404。

## 4. 现场准备顺序

在执行真实硬件动作前依次确认：

1. Approved Hardware Profile / BenchId；
2. Topology Revision；
3. Trial Plan / Version；
4. Change Ticket；
5. Maintenance Window；
6. Operator；
7. 独立 Safety Approver；
8. 急停；
9. 机械互锁；
10. 防护/围栏；
11. HIL 网络与生产网络隔离；
12. 禁止使用生产凭据；
13. 人员区域清空；
14. approved Software Head 已部署到专用 HIL Runner。

任何一项未满足都停止执行。

## 5. Session 操作流程

```text
Create Session
→ Safety Preflight
→ Arm
→ BeginExecution(SelfHostedHil attestation)
→ Execute Trial Plan externally
→ Record real-hardware Step Evidence
→ CompleteExecution
→ Protocol validation
→ Mechanical safety acceptance
→ Site acceptance
→ Accept
```

每个 Step 必须保留 StepId、AssetId、结果、时间、`RealHardwareObserved=true` 与 Evidence SHA-256。

## 6. 异常处理

出现设备、通讯、传感器、互锁、位置或安全异常时：

```text
Abort
→ Stop motion
→ Put PLC outputs into approved safe state
→ Restore interlocks
→ Verify emergency-stop state
→ Clear operator area
→ Record Recovery Evidence
→ Recovered
```

`Recovered` 是终态，同一 Session 禁止重新 Running。重新试验必须新建 Session，并使用 `RecoveryFromSessionId` 关联旧 Session。

## 7. Real HIL Evidence Gate

真实台架执行完成后，在专用 self-hosted Runner 上人工触发：

```text
WCS S9 Real HIL Evidence Gate
```

必填输入：

```text
expected_head=<exact 40-char Software Head>
session_id=<approved SessionId>
bench_id=<approved BenchId>
```

Runner 必须具备：

```text
self-hosted
wcs-hil
```

受保护环境变量/变量提供现场 Runner 本地文件路径：

```text
WCS_HIL_EVIDENCE_MANIFEST
WCS_HIL_EVIDENCE_BUNDLE
```

Gate 不执行设备动作，只验证现场已经产生的真实证据并上传 Artifact。

## 8. Evidence Manifest 最小结构

Schema：

```text
wcs-s9-hil-evidence/v1
```

必须包含：

- SessionId / BenchId / HeadSha；
- RunnerKind / RunnerLabels；
- RealHardwareConnected；
- ProductionNetworkIsolated；
- UsesProductionCredentials=false；
- Operator / SafetyApprover；
- ChangeTicket / MaintenanceWindowId；
- 全部 Preflight 结果；
- 全部 Trial Step 真实结果及 EvidenceSha256；
- ProtocolValidated；
- MechanicalSafetyAccepted；
- SiteAccepted；
- 最终 EvidenceBundleSha256。

## 9. 最终收口

Real HIL Gate 成功后仍需：

1. 核对 Artifact 未过期；
2. 核对 Artifact Digest；
3. 核对 validated summary 的 Head/Bench/Session；
4. 核对 PR Head 未漂移；
5. PR Conversation 固化软件 Gate + Real HIL Gate Evidence；
6. 只有真实 Protocol / Mechanical / Site 均通过才将 PR 从 Draft 标记 Ready；
7. 使用 expected_head_sha 保护合并；
8. 合并后验证 `develop` 已包含该 merge。

## 10. 禁止事项

- 不在 hosted runner 连真实 PLC/RGV；
- 不把 simulation/mock/manual JSON 当真实证据；
- 不保存生产密码/Token；
- 不为通过 CI 降低阈值；
- 不跳过 Abort 后的 Recovery；
- 不在缺机械安全/现场签署时合并为 S9 Completed。
