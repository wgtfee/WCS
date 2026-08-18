# EMS / RGV 统一调度第九阶段 CI 检查清单

Windows CI 必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

新增 Core 测试：

```text
TransportProductionSchedulingTests.cs
TransportProductionDurabilityTests.cs
TransportProductionSafetyTests.cs
```

重点检查：

- UnifiedTransportDispatchEngine 可选门禁构造参数保持旧测试兼容；
- ReliableTransportProductionDispatchService 构造链无 DI 循环；
- 生产派单成功后执行引擎已 Create + Start；
- 每周期 AttemptCount 只增加一次；
- 等待队列恢复不恢复 Assigned 任务；
- 不同车辆命令并行、同一车辆命令顺序下发；
- 决策记录可从 Wcs_TransportJournal 恢复；
- 单轨反向等待和 OccupancyConfirmed 保护；
- 故障接管在重分配前检查物理占用；
- 终态单轨许可仅在物理占用清除后释放；
- Host Controller 的 ChangeConfiguration 审批目标一致；
- Desktop IWcsApiService 与 WcsApiService 方法完全对应；
- Avalonia 嵌套属性绑定、DataGrid 和 UniformGrid 可编译；
- main 分支未修改。
