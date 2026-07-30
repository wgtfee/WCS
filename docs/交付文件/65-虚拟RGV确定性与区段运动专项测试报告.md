# 虚拟 RGV 确定性与区段运动专项测试报告

## 1. 报告状态

阶段：WCS Simulation & Verification v1.0 S3。

当前状态：功能与专项测试已建立，正式验收 Evidence 待最新 exact Head 的首轮 33/33 累计回归完成后回填。本文在 Evidence 回填前不声明 S3 已完成或可替代 HIL/现场验收。

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

累计回归应覆盖 33 条 exact-head 子工作流：历史 25 项、S0/S1/S2 六条专项，以及 S3 两条专项。

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

## 5. 当前已知首轮专项

初始功能 Head `6cea79dd7113f8a1e71ff47c06e06b9cd8ce0c68` 上：

- WCS Simulation Virtual RGV #1：success；
- WCS Simulation RGV Motion Determinism #1：success；
- One Hour Soak #314：success。

这些结果只证明该历史 Head；后续提交产生新 Head 后必须重新运行，不得用于替代最新 exact Head 的正式验收。

## 6. 正式验收条件

首轮功能 Head 必须同时满足：

1. Virtual RGV 专项 success；
2. RGV Motion Determinism 专项 success；
3. S3 Full Regression success；
4. `evidence.json.workflowCount == 33`；
5. `allSuccess == true`；
6. 每条 child `status=completed`、`conclusion=success`；
7. 每条 child `headSha` 等于 expected Head；
8. 结束前 PR Head 未漂移。

首轮通过后回填 Run、Artifact 和 Digest，形成 Evidence Head；Evidence Head 必须再次完成相同 33/33，才允许 PR Ready 和 Squash 合并。

## 7. Evidence 回填区

待首轮 33/33 完成后填写：

```text
Functional Head:
Virtual RGV Run / Artifact / Digest:
RGV Motion Determinism Run / Artifact / Digest:
S3 Full Regression Run / Artifact / Digest:
workflowCount:
allSuccess:
Head verification:
```

## 8. 结论边界

当前结论仅为：S3 功能和专项验证框架已建立。S3 尚未完成两轮 33/33，尚未合入 `develop`，也未完成 HIL、真实协议、机械安全或现场验收。
