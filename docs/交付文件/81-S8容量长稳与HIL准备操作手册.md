# S8 容量长稳与 HIL 准备操作手册

## 1. 使用范围

本文用于开发、复验和排查 WCS Simulation & Verification v1.0 S8。S8 只运行在 `Simulation` / `SimulationLoadTest` 环境，目标是验证容量边界、加速虚拟 8h/24h 长稳、状态守恒、确定性恢复和进入 S9 前的软件侧 readiness。

S8 不是 HIL 执行手册。不得把本文步骤连接到真实 PLC、真实 RGV、现场 MES/SQL、工业网络、真实模型、机械安全回路或生产控制路径。

## 2. 环境前置

必须同时满足：

```text
Simulator.Enabled=true
SimulationGovernance.Enabled=true
Environment=Simulation 或 SimulationLoadTest
Environment!=Production
```

Production 和非批准环境必须返回 404。Production 中仿真默认关闭。

## 3. 关键配置

S8 使用 `SimulationCapacityReadiness` 配置段，并继续受 S0～S7 各模块容量上限约束。修改容量 Profile 时必须同时检查：

- Scenario Engine State/Timeline/Checkpoint 上限；
- Virtual PLC blocks/fault/audit 上限；
- Virtual RGV vehicles/segments/routes 上限；
- Virtual Traffic zones/reservations/waiting/deadlock 上限；
- Virtual External endpoints/requests/fault/audit 上限；
- Virtual Health assets/samples/forecasts/outcomes 上限；
- Virtual Integration missions/segments/audit 上限；
- S8 Profile、Sample、Audit 与虚拟长稳周期上限。

容量预检必须先于资源创建。若 Profile 需要的组合资源超过任一已有硬边界，测试应 fail-closed；不得通过先创建部分资源再回滚的方式绕过预检。

## 4. 本地专项测试

容量专项：

```bash
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj \
  --configuration Release \
  --filter FullyQualifiedName~SimulationCapacityReadinessTests
```

预期：12/12。

HIL readiness 专项：

```bash
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj \
  --configuration Release \
  --filter FullyQualifiedName~SimulationHilReadinessGateTests
```

预期：5/5。

## 5. 8h/24h 长稳说明

S8 的 8h/24h 是确定性的加速虚拟时间：场景引擎推进 virtual offset，不要求测试进程真实等待 8/24 小时。

验证时必须同时检查：

- 最终 Mission 数与定义数一致；
- active reservation / waiting request / deadlock 无泄漏；
- External request exactly-once；
- Health outcome exactly-once；
- PLC 完成/确认 flag 与 mission state 一致；
- State/Sample/Audit 没有超过配置上限；
- Checkpoint restore 后与 uninterrupted run 最终 StateHash 一致；
- Replay 后 StateHash / EvidenceHash 一致。

虚拟 8h/24h 不能替代 `WCS One Hour Soak Load`。S8 Full Regression 必须继续包含该真实墙钟历史门禁。

## 6. CI 门禁

### 6.1 Capacity Long Stability

Workflow：

```text
WCS Simulation Capacity Long Stability
```

必须验证 12/12，并上传 Restore/Build/Test/TRX、资源统计等 Artifact。资源证据用于同 Head 复验，不应解释为现场硬件性能。

### 6.2 HIL Readiness Gate

Workflow：

```text
WCS Simulation HIL Readiness Gate
```

必须验证 5/5，并检查静态隔离与 Production fail-closed。

### 6.3 S8 Full Regression

Workflow：

```text
WCS Simulation S8 Full Regression
```

最终 Evidence 必须同时满足：

```text
workflowCount=43
allSuccess=true
43/43 status=completed
43/43 conclusion=success
43/43 headSha=expected Head
PR Head expected == actual
One Hour Soak success
```

旧 Head 的成功不能替代 latest exact Head。

## 7. Runner 资源波动处理

若 RSS/资源类门禁失败：

1. 先确认业务断言、状态守恒、SQL/HTTP/控制写计数等是否正常；
2. 查看相同 Head 的 Runner resource evidence；
3. 若有充分证据表明是 hosted runner 暂态抖动，可在完全相同 Head 上使用原门槛重跑；
4. 不得降低已有 RSS/性能/容量/安全阈值；
5. 不得删除测试、`continue-on-error` 或复用 stale Head evidence；
6. 若相同 Head 重复失败，应按真实稳定性问题处理并修实现。

## 8. HIL Readiness 判读

只有 software-side prerequisites 满足时，S8 可返回 `ReadyToEnterS9=true`。该字段绝不代表真实 HIL 已通过。

S8 中以下状态必须保持：

```text
RealHilExecuted=false
MechanicalSafetyAccepted=false
SiteAccepted=false
```

进入 S9 前仍需准备真实 PLC/RGV/安全回路、点位与拓扑、工业网络、协议、现场凭据、机械安全规则、真实 MES/SQL endpoint、试运行方案和项目签署。

## 9. Host API 原则

`SimulationCapacityReadinessController` 只能提供只读 inspection/status/report。不得增加以下 API：

- 真实 PLC/RGV 写控制；
- 生产任务取消/派单；
- 路线/路权释放；
- 真实故障注入；
- 真实 HTTP/SQL/Socket 请求；
- 通过 HTTP 直接绕过 Scenario/Replay 启动容量负载。

## 10. 双轮 Evidence 操作

首轮 Functional Head 全绿后记录三条 S8 workflow 的 Run、Artifact ID/Name/Digest。然后只修改 79～81、00、21 文档形成 Evidence Head。

Evidence Head 形成后：

1. 不再修改任何仓库文件；
2. 再跑两条专项；
3. 再跑 43-child S8 Full Regression；
4. 下载并核实最终 Artifact；
5. 确认 PR Head 未漂移；
6. 第二轮 Evidence 只写 PR Conversation；
7. PR 标记 Ready；
8. 使用 `expected_head_sha=Evidence Head` squash merge 到 `develop`；
9. 再验证 PR closed/merged 与 `develop` 包含 merge SHA。

## 11. 回退

如需立即停止仿真验证：

```text
SimulationGovernance__Enabled=false
Simulator__Enabled=false
```

该回退只影响 Simulation，不能作为生产 PLC、调度、机械安全或现场异常的控制手段。
