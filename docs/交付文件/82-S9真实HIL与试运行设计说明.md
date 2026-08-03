# S9 真实 HIL 与试运行设计说明

## 1. 阶段定位

S9 是 WCS Simulation & Verification v1.0 从“软件侧仿真验证”进入“真实硬件在环与受控试运行”的边界阶段。

S8 已证明仓库级软件具备进入 S9 的条件，但 S8 的虚拟 8h/24h、容量、恢复和 43/43 exact-head 证据不能替代真实 PLC、真实 RGV、工业网络、机械互锁和现场验收。S9 只在存在真实硬件台架、维护窗口、安全审批和现场证据时才允许形成 `RealHilExecuted=true`。

S9 基线引用 S8 最终 Evidence Head：

```text
02b202862816a91ff473925bb964e4d2aa2f6470
```

## 2. 第一批实现

```text
Wcs.Simulator/HilVerification
├── HilVerificationContracts.cs
└── HilVerificationRuntime.cs
```

该模块是 HIL 治理与证据状态机，不是硬件驱动。它不会打开 Socket/HTTP/SQL/S7/Snap7 连接，不调用生产 CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch 或真实模型推理。

真实硬件动作必须由独立、现场所有的 HIL Runner/适配层执行，并把不可伪造的运行证据回填到 S9 会话。

## 3. HIL 会话状态机

```text
Defined
  ↓ 安全预检
PreflightPassed
  ↓ 明确 Arm
Armed
  ↓ SelfHostedHil + real hardware attestation
Running
  ↓ 所有计划步骤具有真实硬件通过证据
Completed
  ↓ 协议 + 机械安全 + 现场显式签署
Accepted
```

任何安全预检失败进入 `Rejected`；运行中的异常可以进入 `Aborted`。终态不能通过普通重试绕过。

## 4. 硬件档案

`HilHardwareProfileDefinition` 只记录受控 bench 身份与逻辑资产 ID，不在仓库保存真实 IP、密码、Token、现场拓扑或生产凭据。

硬性要求：

- `ProductionNetworkIsolated=true`；
- `UsesProductionCredentials=false`；
- 至少存在一个控制器资产；
- PLC/RGV 资产 ID 在档案内唯一；
- 真正 endpoint/credential 由现场 HIL Runner 的受保护环境配置管理。

## 5. 双人安全预检

进入 Armed 前必须显式确认：

- 急停已验证；
- 机械互锁已验证；
- 防护/围栏已验证；
- HIL 网络与生产网络隔离；
- 设备处于批准的维护/试运行模式；
- 人员区域清空；
- Operator 与 SafetyApprover 为不同人员。

缺一项即 fail-closed。

## 6. 真实 HIL 执行声明

`BeginExecution` 只接受：

```text
RunnerKind = SelfHostedHil
RealHardwareConnected = true
BenchId = 已批准 HardwareProfile.BenchId
EvidenceBundleSha256 = 合法 SHA-256
```

GitHub hosted runner、普通仿真 runner、虚拟 PLC/RGV 结果均不能把 S9 会话推进到真实 HIL Running。

## 7. 试运行步骤与证据

计划步骤可以描述：

- ConnectivityRead；
- PlcRead；
- ControlledPlcWrite；
- VehicleMove；
- InterlockVerify；
- EmergencyStopVerify；
- RecoveryVerify；
- ExternalAckVerify。

`HilVerificationRuntime` 只记录步骤契约和结果，不执行这些动作。`StepResult` 必须带 `RealHardwareObserved=true`；所有计划步骤最终必须拥有通过证据，且不能存在失败证据，才能进入 `Completed`。

证据记录有数量和长度上限，并按 Sequence 进入规范化 EvidenceHash。

## 8. 最终验收语义

`Completed` 只说明试验计划在真实 bench 上按证据完成，不等于现场接受。

从 `Completed` 进入 `Accepted` 仍必须外部显式提供：

```text
ProtocolValidated=true
MechanicalSafetyAccepted=true
SiteAccepted=true
AcceptedBy=<现场验收人>
EvidenceBundleSha256=<最终证据包哈希>
```

因此仓库 CI 永远不能单独宣告 S9 完成。

## 9. CI 与真实 HIL 的边界

第一条 S9 CI 门禁：`WCS S9 HIL Governance Contract`。

它只验证：

- 12/12 HIL 状态机/安全契约测试；
- Enabled 默认 fail-closed；
- 生产网络/生产凭据拒绝；
- hosted runner 不能冒充真实 HIL；
- 全步骤真实硬件证据要求；
- 协议、机械安全、现场验收必须显式签署；
- HIL 治理层不含真实 IO/生产控制依赖。

真正的 S9 HIL Gate 后续必须运行在带专用标签的 self-hosted HIL Runner，并由现场安全/设备条件满足后人工触发。GitHub hosted runner 只能验证框架，不能替代真实 bench。

## 10. S9 后续工作

S9 将继续补齐：

1. 现场 HIL Runner 接口与受保护配置规范；
2. 只读预检/状态查询，不提供绕过安全门禁的远程控制 API；
3. PLC 通讯、RGV 动作、互锁、急停、恢复、MES Ack 的现场试验计划模板；
4. self-hosted HIL 工作流与 Evidence Bundle 规范；
5. 真实 HIL 运行记录与试运行日报；
6. S9 专项报告与操作手册；
7. 在真实证据满足前 PR 保持 Draft，不以 CI 绿色替代现场签署。

## 11. 安全红线

- 不在 GitHub hosted runner 连接现场设备；
- 不在仓库保存现场真实凭据；
- 不绕过 PLC/设备已有安全联锁；
- 不把仿真、Mock、手工构造 JSON 当作真实 HIL；
- 不因测试失败而降低安全阈值；
- 未获得机械安全、协议和现场显式验收时，S9 不得标记完成。
