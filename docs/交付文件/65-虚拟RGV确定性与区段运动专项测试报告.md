# 虚拟 RGV 确定性与区段运动专项测试报告

## 1. 报告状态

阶段：WCS Simulation & Verification v1.0 S3。

当前状态：功能 Head 首轮 33/33 exact-head 累计回归已完成并核验。本文回填首轮 Run、Artifact 和 Digest；文档提交后形成新的 Evidence Head，必须再次完成同等 33/33 后，才允许 PR Ready 和 Squash 合并。

本文只证明仓库内确定性仿真行为，不替代 HIL、真实协议、制动距离、机械互锁或现场安全验收。

## 2. 验证目标

- 虚拟区段定义与拓扑连续性；
- 虚拟 RGV 定义、在线/离线和统一状态投影；
- 基于虚拟时间的确定性单段和多段运动；
- 车辆速度与区段限速；
- 位置、区段占用和路线完成；
- 载荷装卸约束；
- 电量整数基点与毫米余数一致性；
- Checkpoint 恢复、Replay 和 State Hash；
- Host Production 404、未知运行 404 和只读检查；
- 容量上限与审计环形边界；
- S3 不提前实现 S4 调度、路权、冲突或死锁算法；
- 无真实 RGV、PLC 和控制写入依赖。

## 3. 专项工作流

```text
WCS Simulation Virtual RGV
WCS Simulation RGV Motion Determinism
WCS Simulation S3 Full Regression
```

累计回归覆盖 33 条 exact-head 子工作流：历史 25 项、S0/S1/S2 六条专项，以及 S3 两条专项。

## 4. 已建立测试内容

### 4.1 虚拟 RGV 契约

- 区段、车辆、路线、载荷、在线状态和审计；
- `TransportVehicleSnapshot` 投影；
- Host status、vehicles、segments、occupancy、audit；
- Checkpoint/Replay；
- Production 404 和会话隔离。

### 4.2 运动确定性

- 1000 mm/s 车辆在 1000 mm 区段上的整数毫秒推进；
- 一次推进跨越多个连续区段；
- 区段速度上限约束；
- 路线起点与相邻区段拓扑校验；
- 区段占用断言；
- 电量基点和余数；
- 相同输入相同 State Hash；
- Checkpoint 后继续执行与直接执行结果一致。

### 4.3 阶段边界

专项工作流检查 S3 源码不包含：

```text
UnifiedTransportDispatchEngine
ITransportTrafficCoordinator
TransportTrafficCoordinator
IRouteReservationManager
Deadlock
TryAcquire(
```

S3 只产生运动和占用状态，S4 才验证调度与交通控制。

## 5. 功能 Head 首轮验收

功能 Head：

```text
88573c9d08c325b978c34a1f6ddd3e6c754fc9dc
```

### 5.1 主工作流证据

| 工作流 | Run | Artifact | Digest | 结论 |
|---|---:|---|---|---|
| WCS Simulation Virtual RGV #5 | 30543781346 | `wcs-simulation-virtual-rgv-5` | `sha256:5f1e3f1c12d0de86367fed345ea90206b87ed19fc2af86f6dd7ac5c27b2d97d1` | success |
| WCS Simulation RGV Motion Determinism #5 | 30543781483 | `wcs-simulation-rgv-motion-determinism-5` | `sha256:89cf965c7d800f0de318fcf33c26cb13f1add52bfeb1fff7cfc79c6fe4cf5a44` | success |
| WCS Simulation S3 Full Regression #4 | 30543781598 | `wcs-simulation-s3-full-regression-4` | `sha256:9bf2f32dafb8a8518a6667d5a8220b5ed4ca73221dcd80858a354bec97b61fdc` | success |

### 5.2 Full Regression Evidence 核验

```text
expectedHead = 88573c9d08c325b978c34a1f6ddd3e6c754fc9dc
workflowCount = 33
allSuccess = true
all child status = completed
all child conclusion = success
all child headSha = expectedHead
PR head verification = success
```

33 条子工作流包括 Forecast、Adapter、PLC ML、Anomaly Engine Load/Soak、Telemetry、Windows、E2E、One Hour Soak、Transport Cycle、Health、Governance、Root Cause、Maintenance，以及 Simulation S0～S3 专项。

### 5.3 历史 Load 重跑说明

首轮聚合第一次等待时，`WCS PLC Anomaly Engine Load` 的业务生命周期、SQL 计数和托管 GC 均正确，但 hosted runner 进程 RSS 保留触发物理内存增长门槛。未修改代码、未降低门槛、未使用 `continue-on-error`；同一 exact Head 重跑后该工作流全步骤成功，随后 S3 Full Regression 重新汇总并通过 33/33。

## 6. Evidence Head 二次验收条件

本次文档提交产生新的 Evidence Head 后，必须同时满足：

1. Virtual RGV 专项 success；
2. RGV Motion Determinism 专项 success；
3. S3 Full Regression success；
4. `evidence.json.workflowCount == 33`；
5. `allSuccess == true`；
6. 每条 child `status=completed`、`conclusion=success`；
7. 每条 child `headSha` 等于 Evidence Head；
8. 结束前 PR Head 未漂移；
9. 第二轮 Artifact 和 Digest 已读取；
10. PR 仍为 open、draft、mergeable，随后才可 Ready 和 Squash。

## 7. 结论边界

当前结论是：S3 功能 Head 的首轮仓库验收已完成，文档证据已回填。S3 尚未完成 Evidence Head 第二轮 33/33，尚未合入 `develop`，也未完成 HIL、真实协议、机械安全或现场验收。
