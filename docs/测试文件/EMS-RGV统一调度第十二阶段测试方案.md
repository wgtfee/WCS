# EMS/RGV 统一调度第十二阶段测试方案

## 1. 测试目标

验证最后阶段的离线仿真、历史回放、策略 A/B、批量优化、拥堵预测、容量压力、故障注入和最终验收报告满足：

- 结果确定性；
- 与生产控制层隔离；
- 可持久化、可回放；
- 结果可解释；
- 不自动应用推荐参数；
- 不替代现场人工验收。

## 2. 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportSimulationTests.cs
```

覆盖：

1. 相同场景、策略和 Seed 产生完全相同的任务轨迹、指标和拥堵预测；
2. 仿真前后生产车辆和站点稳定业务字段不变化；
3. 紧急任务场景下 DeadlineFirst 优于基准策略；
4. 离线和命令失败故障只作用于场景副本；
5. 历史 ProductionQueue Journal 能生成回放场景；
6. 相同任务率下增加车辆不会降低完成任务数；
7. 最终验收按显式阈值生成 Passed 或非 Passed；
8. 批量优化不修改生产整定参数；
9. 过载场景能产生 Heavy 拥堵预测。

## 3. Core 测试命令

```powershell
dotnet restore src/Wcs.Core.Tests/Wcs.Core.Tests.csproj
dotnet build src/Wcs.Core.Tests/Wcs.Core.Tests.csproj -c Release --no-restore
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj `
  -c Release `
  --no-build `
  --logger "trx;LogFileName=wcs-core-tests.trx" `
  --results-directory TestResults
```

## 4. 确定性测试

对同一场景连续运行两次，比较：

- `TransportSimulationMetrics`；
- `TransportSimulationTaskResult[]`；
- `TransportCongestionForecastPoint[]`。

不比较 RunId、StartedAtUtc 和 CompletedAtUtc。

故障概率使用固定 SHA-256 输入，不使用共享 `Random` 状态。

## 5. 生产隔离测试

仿真前后比较：

- 车辆注册表；
- 站点容量和占用；
- 生产整定参数；
- 生产等待队列；
- 执行任务；
- 活动路权；
- PLC 驱动诊断；
- PLC 点位访问记录。

预期：除仿真 Journal 和 Telemetry 外，生产状态无变化。

站点 `UpdatedAtUtc` 是查询展示时间，不作为稳定业务字段比较。

## 6. 历史回放测试

准备：

1. 写入多个 `ProductionQueue` Journal；
2. 同一 RequestId 写入多个版本；
3. 设置不同 OccurredAtUtc 和 UpdatedAtUtc；
4. 配置回放时间范围。

预期：

- 只读取时间范围内记录；
- 同一 RequestId 只保留最新版本；
- ArrivalOffsetSeconds 按原入队时间计算；
- 最大任务数生效；
- 不修改原 Journal。

## 7. 策略 A/B 测试

至少构造：

- 高优先级普通任务；
- 低基础优先级但交期紧急任务；
- 长时间等待任务；
- 相同目的地批量任务；
- 拥堵站点任务。

分别验证：

- BaselineDynamicPriority；
- AgingFirst；
- DeadlineFirst；
- CongestionAware；
- BalancedBatch。

报告必须包含排名、推荐 PolicyId、每个 RunId 和相对基准说明。

## 8. 故障注入测试

### 8.1 车辆离线

故障窗口覆盖任务开始时间。

预期：车辆可用时间推迟到故障结束，不修改真实车辆在线状态。

### 8.2 心跳冻结

行为与车辆离线一致，但报告保留独立故障类型。

### 8.3 站点封锁

预期：任务等待到封锁结束；封锁超出仿真窗口时任务失败。

### 8.4 交通资源封锁

预期：涉及该资源的任务等待；无关任务不受影响。

### 8.5 驱动延迟

预期：任务周期增加，PLC 实际响应指标不变化。

### 8.6 命令失败

使用 FailureProbability=1。

预期：目标任务确定性失败，重复运行结果一致，不向真实 PLC 下发命令。

## 9. 容量压力测试

组合：

```text
车辆数：1, 2, 3, 4
任务率：30, 60, 90, 120 /h
重复次数：3
仿真时长：60 分钟
```

检查：

- 每个组合均生成结果；
- 平均指标计算正确；
- 相同任务率下更多车辆不应降低完成数；
- 最大可持续任务率来自 Sustainable 组合；
- 推荐车辆数为该任务率下最少车辆数；
- 容量测试不写入每个内部重复 Run，只持久化最终 Benchmark。

## 10. 最终验收报告测试

显式设置两组门槛：

### 宽松门槛

预期全部通过，状态为 Passed。

### 严格门槛

预期存在失败项，状态为 Conditional 或 Failed。

报告必须始终包含现场人工检查：

- PLC 和点位版本；
- 急停和断线；
- 单轨和闭塞；
- 蓝绿切换和回退；
- 权限审批和审计；
- 多部门签署。

## 11. Host 构建与 API

```powershell
dotnet restore src/Wcs.Host/Wcs.Host.csproj
dotnet build src/Wcs.Host/Wcs.Host.csproj -c Release --no-restore
```

检查路径：

```text
/api/transport/simulation/summary
/api/transport/simulation/scenarios/current
/api/transport/simulation/scenarios/history
/api/transport/simulation/runs
/api/transport/simulation/comparisons
/api/transport/simulation/optimizations
/api/transport/simulation/capacity-benchmarks
/api/transport/simulation/acceptance-reports
/api/transport/simulation/report/export
```

计算型接口必须记录认证执行人。

## 12. Desktop 构建

```powershell
dotnet restore src/Wcs.Desktop/Wcs.Desktop.csproj
dotnet build src/Wcs.Desktop/Wcs.Desktop.csproj -c Release --no-restore
```

检查：

- `/TransportSimulation` 路由可解析；
- 仿真、策略、优化、容量、拥堵和验收页签可显示；
- 嵌套指标绑定有效；
- 页面仅有刷新按钮；
- 不存在参数应用、PLC 注入或投产按钮。

## 13. 现场最终验收

CI 通过后仍需现场执行：

1. 导入正式 PLC 点位表；
2. 校验车辆位置和闭塞区；
3. 单车空载运行；
4. 单车负载运行；
5. 多车同向运行；
6. 单轨相向等待；
7. 站点满载与恢复；
8. 车辆离线和心跳冻结；
9. 急停、断电和重启；
10. WCS Host 重启对账；
11. 数据库恢复；
12. 蓝绿切换、Drain 和回退；
13. 连续生产班次压力验证；
14. 导出最终验收报告并多部门签署。
