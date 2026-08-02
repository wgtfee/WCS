# S8 容量长稳与 HIL 准备设计说明

## 1. 阶段定位

S8 是 WCS Simulation & Verification v1.0 的“容量长稳/HIL 准备”阶段，基于 S0～S7 已完成的统一治理、场景引擎、虚拟 PLC、虚拟 RGV、交通/死锁、外部依赖故障、健康/RUL 和全系统集成恢复能力。

S8 目标是证明仓库级软件在受控仿真环境下具备：

- 明确且可验证的容量边界；
- 加速虚拟时间 8h / 24h 长稳场景；
- 状态、队列、路权、请求、健康结果的守恒；
- Checkpoint / Restore / Replay 的确定性；
- 容量达到上限时 fail-closed，不产生半初始化资源；
- 进入 S9 真实 HIL 前的软件侧准备清单。

S8 不执行真实 HIL，不连接真实 PLC/RGV，不证明机械安全、工业网络、现场协议、真实设备性能或投产验收。

## 2. 实现模块

```text
Wcs.Simulator/CapacityReadiness
├── CapacityReadinessContracts.cs
└── CapacityReadinessRuntime.cs
```

Host 只增加只读 `SimulationCapacityReadinessController`，运行时继续复用 S1 `SimulationStateStore` 与 S7 `VirtualIntegrationRuntime`，没有创建第二套状态中心，也没有新增生产控制算法。

核心契约包含有界 `CapacityReadinessOptions`、Capacity Profile、Sample/Audit、容量预检、守恒检查和 `HilReadinessSnapshot`。容量预检在创建 S2～S7 虚拟资源之前执行；任何组合容量超限都 fail-closed，禁止形成半初始化资源。

## 3. 容量与长稳原则

- 所有配置都有默认值与硬上限；
- 8h/24h 指“加速虚拟时间场景”，不是 GitHub Runner 连续运行 8/24 小时；
- CI 继续保留 `WCS One Hour Soak Load` 作为真实墙钟历史门禁；
- 容量专项记录测试耗时与 Runner 可获得的 RSS / 上下文切换等资源证据，并通过测试覆盖 GC/线程、队列与守恒量；
- 容量失败必须区分真实软件问题与 Runner 暂态抖动，禁止通过降低原门槛制造通过；
- 所有 State、Sample、Audit、Mission、Segment、Reservation、External Request、Health Outcome 等均受已有 S0～S7 容量契约或 S8 Profile 约束。

## 4. HIL Readiness 语义

S8 的 `ReadyToEnterS9` 只表示“仓库级软件侧准备条件满足”。无论 S8 是否通过，以下字段在 S8 永远为 false：

```text
RealHilExecuted=false
MechanicalSafetyAccepted=false
SiteAccepted=false
```

因此 S8 通过不能解释为真实 HIL、机械安全、现场点位/拓扑、工业网络/协议、真实 PLC/RGV/MES/SQL/model endpoint 或正式投产已验收。上述工作只允许在 S9/项目级验收中完成。

## 5. 三条验收门禁

1. `WCS Simulation Capacity Long Stability`
   - 12/12 tests；
   - 容量边界、8h/24h 虚拟长稳、Checkpoint/Restore、Replay/Hash、守恒与有界状态；
   - 输出 Restore/Build/Test/TRX 与 Runner resource evidence Artifact。

2. `WCS Simulation HIL Readiness Gate`
   - 5/5 tests；
   - Production/非批准环境 404；
   - 无真实 PLC/S7/Snap7/Socket/HTTP/SQL/ONNX 控制依赖；
   - 无写控制 API；
   - 明确 S8 仅为进入 S9 的 software-side gate。

3. `WCS Simulation S8 Full Regression`
   - S7 41 条 exact-head child + 两条 S8 专项；
   - `workflowCount=43`；
   - `allSuccess=true`；
   - 43 个 child 全部 `completed/success`；
   - 43 个 child `headSha` 精确等于验收 Head；
   - 结束前 PR Head `expected == actual`；
   - 包含 `WCS One Hour Soak Load`。

## 6. 首轮 Functional Head Evidence

首轮 Functional Head：

```text
7638d8b2364a5c46277a74476003ba9b35c480fa
```

### 6.1 Capacity Long Stability

```text
Workflow: WCS Simulation Capacity Long Stability
Run: 30761181440 (#42)
Tests: 12/12 success
Artifact ID: 8837654072
Artifact: wcs-simulation-capacity-long-stability-42
Digest: sha256:d75faa0b975a9eb995ef9bdfeb5e2ba7486a3613e6efbc8cf4d2818e29e09504
Expired: false
Test duration: 703 ms
Measured wall time: 2.47 s
Maximum RSS: 109572 KB
Voluntary context switches: 900
Involuntary context switches: 2015
```

该资源证据来自 hosted runner 的 `/usr/bin/time -v`，用于仓库级复验，不代表真实 HIL 设备资源曲线。

### 6.2 HIL Readiness Gate

```text
Workflow: WCS Simulation HIL Readiness Gate
Run: 30761181420 (#41)
Tests: 5/5 success
Artifact ID: 8837647299
Artifact: wcs-simulation-hil-readiness-41
Digest: sha256:de8dcb512075b5f7add476580a144c43a1b8fa16620c6d6c6db834f326021b29
Expired: false
Test duration: 30 ms
```

### 6.3 S8 Full Regression

```text
Workflow: WCS Simulation S8 Full Regression
Run: 30761181383 (#40)
Artifact ID: 8838270990
Artifact: wcs-simulation-s8-full-regression-40
Digest: sha256:94d36f32eefe7830c4b2219ff80156ef507f3ef8fe8bc4cb0f00b5dbdd406fdc
Expired: false
workflowCount=43
allSuccess=true
43/43 exact-head success
One Hour Soak Run 30761181263 success
Head verification expected == actual == 7638d8b2364a5c46277a74476003ba9b35c480fa
```

## 7. 双轮验收

沿用 S0～S7 统一流程：

```text
Functional Head
→ 两条 S8 专项
→ 43-child exact-head 全回归
→ 首轮 Artifact/Digest 固化
→ 仅文档 Evidence 提交
→ Evidence Head
→ 两条 S8 专项再跑
→ 43-child exact-head 再跑
→ 第二轮 Artifact/Digest 固化
→ PR Conversation 记录最终 Evidence
→ Ready
→ Squash Merge develop
```

本文与 80、81、00、21 的文档回填完成后形成最终 Evidence Head。该 Head 形成后不得再修改仓库文件。

## 8. 安全边界

- Production 和代码默认仿真关闭；
- 只允许 `Simulation` / `SimulationLoadTest`；
- 不连接真实 PLC、RGV、MES、SQL、模型或工业网络；
- 不写生产 PLC，不停机，不取消任务，不修改真实路线/路权/车辆选择/派单；
- Host S8 API 只读，不能通过 API 启动容量压测、注入真实故障或执行控制写；
- S8 通过只表示“具备进入 S9 的软件侧准备条件”，不表示 S9 已通过。
