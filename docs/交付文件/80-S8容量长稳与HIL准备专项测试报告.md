# S8 容量长稳与 HIL 准备专项测试报告

## 1. 报告范围

本报告记录 WCS Simulation & Verification v1.0 S8 首轮 Functional Head 的仓库级专项验证。S8 验证容量边界、虚拟 8h/24h 长稳、确定性恢复与 software-side HIL readiness；不执行真实 HIL，不代表机械安全、工业网络、现场点位、真实 PLC/RGV/MES/SQL/model endpoint 或投产验收通过。

首轮 Functional Head：

```text
7638d8b2364a5c46277a74476003ba9b35c480fa
```

## 2. Capacity Long Stability

```text
Workflow: WCS Simulation Capacity Long Stability
Run Number: 42
Run ID: 30761181440
Conclusion: success
Tests: 12/12 passed
Artifact ID: 8837654072
Artifact Name: wcs-simulation-capacity-long-stability-42
Artifact Digest: sha256:d75faa0b975a9eb995ef9bdfeb5e2ba7486a3613e6efbc8cf4d2818e29e09504
Expired: false
```

测试覆盖：

- Profile/Options 有界校验；
- 容量预检在资源创建前 fail-closed；
- 超限时不产生半初始化 S2～S7 虚拟资源；
- 加速虚拟 8h 场景；
- 加速虚拟 24h 场景；
- Mission / State / Reservation / Waiting / Request / Outcome 守恒；
- Checkpoint / canonical state restore；
- Replay / final StateHash / EvidenceHash；
- Sample/Audit/State 有界；
- software-side readiness evidence 的容量前置条件。

Runner 资源 Evidence：

```text
Test duration: 703 ms
Measured wall time: 2.47 s
Maximum resident set size: 109572 KB
Voluntary context switches: 900
Involuntary context switches: 2015
Exit status: 0
```

这些数值来自 GitHub hosted runner，仅作为相同代码 Head 的仓库级可复验证据，不是 HIL 工控机或现场硬件性能结论。

## 3. HIL Readiness Gate

```text
Workflow: WCS Simulation HIL Readiness Gate
Run Number: 41
Run ID: 30761181420
Conclusion: success
Tests: 5/5 passed
Artifact ID: 8837647299
Artifact Name: wcs-simulation-hil-readiness-41
Artifact Digest: sha256:de8dcb512075b5f7add476580a144c43a1b8fa16620c6d6c6db834f326021b29
Expired: false
Test duration: 30 ms
```

门禁确认：

- Production/非批准环境保持 404；
- Host 仅暴露 read-only S8 inspection/status/report；
- S8 不提供启动真实容量压测、真实故障注入或生产控制写的 API；
- S8 模块不依赖真实 PLC/S7/Snap7、Socket、HttpClient、SQL/SqlSugar、生产 CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic control、真实 ONNX/Forecast runtime；
- `ReadyToEnterS9` 仅为 software-side gate；
- `RealHilExecuted=false`；
- `MechanicalSafetyAccepted=false`；
- `SiteAccepted=false`。

## 4. S8 Full Regression

```text
Workflow: WCS Simulation S8 Full Regression
Run Number: 40
Run ID: 30761181383
Conclusion: success
Artifact ID: 8838270990
Artifact Name: wcs-simulation-s8-full-regression-40
Artifact Digest: sha256:94d36f32eefe7830c4b2219ff80156ef507f3ef8fe8bc4cb0f00b5dbdd406fdc
Expired: false
workflowCount=43
allSuccess=true
43/43 child status=completed
43/43 child conclusion=success
43/43 child headSha=7638d8b2364a5c46277a74476003ba9b35c480fa
PR Head expected == actual
```

完整矩阵保留 S7 的 41 条历史 child，并加入两条 S8 专项，共 43 条。`WCS One Hour Soak Load` 在同一 Head 上 Run `30761181263` 成功，因此虚拟 8h/24h 证据没有替代历史真实墙钟 Soak 门禁。

## 5. 首轮结论

S8 Functional Head 的两条专项与 43-child exact-head 累计回归全部通过。首轮结论仅允许解释为：仓库级 Simulation S8 软件能力满足进入 Evidence Head 二次复验的条件。

本结论不能解释为真实 HIL 已执行或通过。真实 HIL、机械安全、工业网络/协议、现场拓扑/点位、凭据、实车/真实 PLC、真实 MES/SQL/模型、故障注入安全和试运行签署仍属于 S9/项目级验收。

## 6. 第二轮要求

文档 79～81、00、21 回填完成后形成 Evidence Head。该 Head 冻结后必须重新执行：

1. `WCS Simulation Capacity Long Stability`；
2. `WCS Simulation HIL Readiness Gate`；
3. `WCS Simulation S8 Full Regression`。

只有第二轮仍满足 12/12、5/5、43/43 exact-head、`workflowCount=43`、`allSuccess=true`、One Hour Soak success、PR Head 未漂移并核实新 Artifact/Digest，PR #41 才可 Ready 并 squash merge 到 `develop`。
