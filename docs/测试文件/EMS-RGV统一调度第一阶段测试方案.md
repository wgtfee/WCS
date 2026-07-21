# EMS / RGV 统一调度第一阶段测试方案

## 1. 测试目标

验证统一调度内核在单实例、内存运行模式下具备以下关键性质：

- 车辆状态不会被旧快照覆盖。
- 车辆类型和能力过滤正确。
- 候选车辆选择结果稳定。
- 路段预留满足全有或全无。
- 过期预留能够释放。
- 派单请求幂等。
- 任务完成后车辆和路段恢复。
- 没有可用车辆时不产生残留状态。

## 2. 测试范围

### 包含

- `InMemoryTransportVehicleRegistry`
- `DefaultTransportVehicleSelector`
- `InMemoryRouteReservationManager`
- `UnifiedTransportDispatchEngine`
- `TopologyGraph` 与 `TransportRouteCenter` 的集成调用

### 不包含

- PLC/EMS/RGV 真实通信。
- 数据库持久化。
- EventBus、AlarmCenter、SignalR。
- 多实例和分布式锁。
- 设备运动学与制动距离。
- 性能压测和长时间稳定性测试。

## 3. 单元测试用例

| 编号 | 用例 | 前置条件 | 预期结果 |
|---|---|---|---|
| UT-01 | 拒绝旧版本车辆快照 | 已存在 Version=2，再写 Version=1 | Upsert=false，原位置不变 |
| UT-02 | 类型与能力过滤 | EMS 有 Lift，RGV 无 Lift | 只返回 EMS |
| UT-03 | 最近车辆优先 | EMS 距取货点 3 段，RGV 距 1 段 | RGV 排名第一 |
| UT-04 | 原子路段预留 | TASK-1 占 E1/E2，TASK-2 请求 E2/E3 | TASK-2 全部失败，无部分预留 |
| UT-05 | 过期 Lease 清理 | E1 预留已到期 | 清理后 E1 可再次预留 |
| UT-06 | 无匹配车辆 | 只存在 RGV，请求只允许 EMS | 派单失败，无 Assignment |
| UT-07 | 请求幂等 | 同一 RequestId 调用两次 | 返回同一 Assignment，只存在一个预留 |
| UT-08 | 完成释放 | 派单成功后调用 Complete | 路段为空，车辆恢复 Idle，任务数为 0 |

## 4. 测试拓扑

```text
N1 --E1--> N2 --E2--> N3 --E3--> N4 --E4--> N5
```

默认任务：

- 取货点：N4
- 目标点：N5
- EMS-01 当前位置：N1
- RGV-01 当前位置：N3

因此 EMS 空驶权重为 3，RGV 空驶权重为 1，载货路径为 E4。

## 5. 执行方式

在仓库根目录执行：

```bash
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj
```

只执行本测试类：

```bash
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj \
  --filter FullyQualifiedName~UnifiedTransportDispatchEngineTests
```

生成覆盖率数据：

```bash
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj \
  --collect:"XPlat Code Coverage"
```

## 6. 验收标准

第一阶段代码进入下一阶段前必须满足：

1. 本文列出的单元测试全部通过。
2. 不允许出现随机失败。
3. 同一请求重复执行不得增加活动预留数量。
4. 任一失败分支不得留下车辆 `Executing` 或孤立预留。
5. 车辆旧版本快照不得覆盖新版本状态。
6. 测试代码不得依赖外部数据库、PLC、网络或固定时间等待。

## 7. 后续测试计划

### 第二阶段：Adapter 与执行状态机

- PLC 命令写入成功、失败、超时。
- 相同命令号重复下发。
- 回执重复、乱序和延迟。
- 通信中断与恢复。
- 任务暂停和人工接管。

### 第三阶段：交通控制

- 单路线追车。
- 相反方向申请同一闭塞区段。
- 路口冲突矩阵。
- 死锁环检测。
- 回退点选择。
- 故障节点绕行。

### 第四阶段：恢复与高可用

- 进程在预留后、下发前崩溃。
- PLC 已执行但数据库未确认。
- 数据库有任务但 PLC 无任务。
- 主备切换期间重复请求。
- 快照恢复与事件重放一致性。

## 8. 当前验证说明

本次提交环境没有安装 .NET SDK，因此无法在提交前实际运行 `dotnet test`。代码和测试已按仓库现有目标框架与 xUnit 结构编写，提交后应由本地开发环境或 CI 执行上述命令完成编译验证。
