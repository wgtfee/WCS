# EMS/RGV 统一调度第十二阶段 CI 检查清单

## Core Tests

- [ ] `Simulation_SameScenarioPolicyAndSeed_IsDeterministic`
- [ ] `Simulation_DoesNotMutateProductionVehicleOrStationState`
- [ ] `StrategyComparison_DeadlineFirstBeatsBaselineWhenUrgentTaskWouldMiss`
- [ ] `FaultInjection_IsDeterministicAndDoesNotWriteProductionDriver`
- [ ] `HistoricalReplay_BuildsScenarioFromPersistedQueueRecords`
- [ ] `CapacityBenchmark_MoreVehiclesDoNotReduceCompletedTasksAtSameRate`
- [ ] `CapacityBenchmark_DrainsTailWithoutTreatingOutstandingTasksAsFailures`
- [ ] `AcceptanceReport_UsesExplicitThresholds`
- [ ] `BatchOptimization_ReturnsRecommendationWithoutChangingProductionTuning`
- [ ] `CongestionForecast_ReportsHeavyLevelForOverloadedFleet`
- [ ] `CapacityGuard_RejectsDangerousTaskRate`
- [ ] `SimulationHistory_RestoresFromJournalAfterRestart`
- [ ] `QueueSnapshotRead_DoesNotRefreshPriorityAfterTuningChange`
- [ ] 第一至第十一步全部既有测试继续通过

## Core Static Checks

- [ ] `TransportSimulationService` DI 依赖完整
- [ ] `SafeTransportSimulationService` DI 依赖完整
- [ ] `TransportSimulationOptions` 安全范围有效
- [ ] 单次可执行仿真场景最多 5,000 个任务
- [ ] 单场景车辆、站点和故障数量受限
- [ ] 仿真窗口最多 30 天
- [ ] 任务数量乘以预测时间桶不超过 5,000,000
- [ ] 历史回放窗口最多 30 天
- [ ] 历史 Journal 最多查询 50,000 条，并最多输出 5,000 个任务
- [ ] 容量网格最多 200 个组合
- [ ] 容量任务率不超过 10,000/h
- [ ] 单个容量点任务数不超过 5,000
- [ ] 容量估算任务总量不超过 250,000
- [ ] 同一时刻仅允许一个容量压力任务
- [ ] 容量任务在到达观察窗截止后继续排空
- [ ] 截止时未清任务与真实失败分别统计
- [ ] 容量吞吐和利用率不包含观察窗后的排空时间
- [ ] 相同任务/故障/Seed 使用相同故障样本
- [ ] 运行时 `GetQueue()` 使用纯快照，不刷新优先级或清理终态任务
- [ ] 真实 `DispatchCycleAsync()` 继续执行优先级刷新和终态清理
- [ ] 仿真不调用 `ITransportCommandDispatcher`
- [ ] 仿真不调用 `ITransportPlcAccessor`
- [ ] 仿真不调用 `ITransportExecutionEngine.Create/Start`
- [ ] 优化不调用 `ITransportProductionTuningService.SaveAsync`

## Host Build

- [ ] `TransportSimulationController` 编译通过
- [ ] 当前场景生成 API 编译通过
- [ ] 历史回放 API 编译通过
- [ ] 仿真运行 API 编译通过
- [ ] 策略对比 API 编译通过
- [ ] 容量压力 API 编译通过
- [ ] 最终验收 API 编译通过
- [ ] 报告导出 API 编译通过
- [ ] `TransportSimulationInitializationHostedService` 编译通过
- [ ] 不新增数据库表或迁移

## Desktop Build

- [ ] `TransportSimulationApiService` 编译通过
- [ ] `TransportSimulationViewModel` 编译通过
- [ ] `TransportSimulationView.axaml` 编译通过
- [ ] `/TransportSimulation` 路由可解析
- [ ] 嵌套 Metrics 绑定有效
- [ ] 页面仅提供刷新，不提供生产写操作

## 最终安全回归

- [ ] 仿真前后生产车辆状态不变
- [ ] 仿真前后站点业务状态不变
- [ ] 仿真前后生产整定参数不变
- [ ] 仿真不改变等待队列优先级、状态和更新时间
- [ ] 仿真不改变活动任务和路权
- [ ] 故障注入不触发真实 PLC 写入
- [ ] 验收 Passed 仍保留现场人工检查清单
- [ ] `main` 分支保持不变
