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

## 2. 计划新增模块

```text
Wcs.Simulator/CapacityReadiness
├── CapacityReadinessOptions
├── CapacityProfileDefinition
├── CapacityProfileSnapshot
├── CapacitySample
├── CapacityAuditRecord
├── CapacityReadinessRuntime
└── HilReadinessSnapshot
```

运行时继续复用 S1 `SimulationStateStore` 和 S7 `VirtualIntegrationRuntime`，不得创建第二套状态中心或绕过 S0/S1 治理。

## 3. 容量与长稳原则

- 所有配置都有默认值与硬上限；
- 8h/24h 指“虚拟时间场景”，不是 GitHub Runner 连续运行 8/24 小时；
- CI 仍保留现有 One Hour Soak 作为真实墙钟长稳历史门禁；
- 容量专项需同时记录测试耗时、进程 RSS/GC/线程/句柄（可获得平台）、队列和守恒量；
- 容量失败必须区分真实软件问题与 Runner 暂态抖动，禁止通过降低原门槛制造通过。

## 4. S8 计划门禁

1. `WCS Simulation Capacity Long Stability`
   - 容量边界；
   - 加速虚拟 8h；
   - 加速虚拟 24h；
   - Checkpoint/Restore；
   - Replay/FinalStateHash；
   - 状态、队列、路权、外部请求、健康结果守恒；
   - 有界 State/Audit/Sample；
   - 运行时资源指标 Artifact。

2. `WCS Simulation HIL Readiness Gate`
   - Production/非批准环境 404；
   - 无真实 PLC/S7/Snap7/Socket/HTTP/SQL/ONNX 控制依赖；
   - 无写控制 API；
   - 8h/24h 与容量 Evidence 齐备；
   - S9 所需外部项明确列为未执行，不允许把 S8 误标为 HIL 已通过。

3. `WCS Simulation S8 Full Regression`
   - 在 S7 41 条 exact-head child 基础上加入上述两条 S8 专项；
   - 目标 child 数为 43；
   - `workflowCount=43`、`allSuccess=true`；
   - 43 个 child 全部 `completed/success` 且 `headSha` 精确等于当前验收 Head；
   - 结束前 PR Head 必须再次等于 expected Head。

## 5. 双轮验收

沿用 S0～S7 统一流程：

```text
功能 Head
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

Evidence Head 形成后不得再修改仓库文件。

## 6. 安全边界

- Production 和代码默认仿真关闭；
- 只允许 `Simulation` / `SimulationLoadTest`；
- 不连接真实 PLC、RGV、MES、SQL、模型或工业网络；
- 不写生产 PLC，不停机，不取消任务，不修改真实路线/路权/车辆选择/派单；
- S8 通过只表示“具备进入 S9 的软件侧准备条件”，不表示 S9 已通过。
