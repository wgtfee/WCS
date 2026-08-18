# EMS/RGV 统一调度第十一步 CI 检查清单

## Core Tests

- [ ] `TransportResilienceTests.InMemoryBackupStorage_TrimsOldestItems`
- [ ] `TransportResilienceTests.CreateBackup_ProducesValidSha256Payload`
- [ ] `TransportResilienceTests.ValidateBackup_RejectsTamperedPayload`
- [ ] `TransportResilienceTests.PrepareRestore_ImportsSnapshotWithoutApplyingRuntimeState`
- [ ] `TransportResilienceTests.IsolatedDrill_DoesNotMutateVehicleRegistry`
- [ ] `TransportResilienceTests.Preflight_ReportsMissingRealPlcDiagnosticAsCritical`
- [ ] 第十阶段可观测性与 TraceId 相关测试继续通过
- [ ] 第九阶段生产调度与持久化测试继续通过

## Host Build

- [ ] `TransportResilienceController` 编译通过
- [ ] `TransportResilienceService` 依赖可由 DI 完整解析
- [ ] `FileTransportLogicalBackupStorage` 编译通过
- [ ] `TransportResilienceOptions` 能从配置加载
- [ ] `TransportReadinessHostedService` 编译通过
- [ ] `TransportAutomaticBackupHostedService` 编译通过
- [ ] `/health/ready` 可解析生产就绪报告
- [ ] 不新增数据库表或迁移

## Desktop Build

- [ ] `TransportResilienceApiService` 编译通过
- [ ] `TransportResilienceViewModel` 编译通过
- [ ] `TransportResilienceView.axaml` 编译通过
- [ ] 菜单路由 `/TransportResilience` 可解析
- [ ] 页面不包含恢复、PLC 写入或故障注入按钮

## 安全回归

- [ ] 备份创建不阻塞 PLC 轮询和调度控制闭环
- [ ] 恢复准备不修改当前运行配置
- [ ] 活动任务、路权和命令不从备份自动写回
- [ ] 隔离演练不修改生产车辆、路权或 PLC 状态
- [ ] `main` 分支保持不变
