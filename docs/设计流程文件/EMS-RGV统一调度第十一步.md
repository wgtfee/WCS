# EMS/RGV 统一调度第十一步：生产韧性、逻辑备份与恢复演练

## 1. 阶段目标

第十一步把第十阶段的可观测性结果转化为可执行的生产运维能力，解决以下问题：

1. 系统当前是否具备上线或继续运行条件；
2. 配置、点位、状态和关键日志是否有可校验的离线备份；
3. 备份损坏、版本不兼容或包含活动任务时能否提前识别；
4. 故障恢复流程能否在不影响生产控制层的情况下反复演练；
5. 恢复时如何避免把历史任务、路权或 PLC 命令直接写回现场。

本阶段仍遵守确定性控制边界：

- 不使用 AI 参与控制或恢复判断；
- 不在演练中修改真实车辆、路权、任务或 PLC 点位；
- 不从逻辑备份自动恢复活动任务和运动命令；
- 真正配置回滚继续使用 `ChangeConfiguration` 双人审批；
- 所有备份适用于离线现场，不依赖云服务。

## 2. 总体流程

```text
运行时状态 / SQL / PLC 诊断 / 健康评分
                │
                ▼
        Production Readiness
                │
        ┌───────┴────────┐
        ▼                ▼
 Operational Baseline  Logical Backup
        │                │
        │          SHA-256 + Schema
        │                │
        └───────┬────────┘
                ▼
       Backup Validation
                │
        ┌───────┴────────┐
        ▼                ▼
 Isolated Recovery Drill  Prepare Restore
                              │
                              ▼
                 Configuration Snapshot
                              │
                              ▼
                 Existing Approval/Rollback
```

## 3. 生产就绪检查

`TransportResilienceService.RunPreflightAsync` 检查：

- 运行配置是否已保存；
- 车辆、驱动和交通资源标识符是否重复；
- 启用车辆是否具有对应驱动和点位映射；
- 真实 PLC 点位映射必填项是否完整；
- PLC 驱动是否连接、在线且状态未过期；
- SQL 运行状态存储是否可读取；
- 最近三方一致性报告是否有效；
- 运输健康评分是否为 Unhealthy；
- 是否存在配置安全快照；
- 最近逻辑备份是否过期；
- 是否存在失败、超时或未完成命令。

结果分为：

- Information：通过项；
- Warning：可以继续运行，但上线前应处理；
- Error：生产就绪失败；
- Critical：立即阻断生产就绪。

`/health/ready` 同时消费最新生产就绪报告：

- Critical 或 Error：Unhealthy；
- Warning：Degraded；
- 全部通过：Healthy。

首次自动检查延迟 15 秒，避免 Host 启动、SQL 恢复和 PLC 首轮轮询尚未稳定时产生瞬时误判。

## 4. 运行基线

运行基线记录：

- 运行配置版本；
- 生产整定版本；
- 车辆总数和在线数；
- 活动任务、路权、命令数量；
- PLC 点位映射和在线驱动数量；
- 生产运输队列长度；
- 健康评分；
- 最近一致性报告；
- 最近生产就绪报告。

基线写入 `Wcs_TransportJournal`，类别为 `OperationalBaseline`。

## 5. 逻辑备份内容

每份逻辑备份包含：

- `TransportRuntimeConfiguration`；
- `TransportProductionTuningOptions`；
- 生产站点定义；
- 单轨会车区段定义；
- PLC 点位映射；
- 车辆、执行、路权和命令运行快照；
- 最近关键 Journal 记录；
- 创建时运行基线。

备份文件默认目录：

```text
data/transport-backups
```

目录按 `AppContext.BaseDirectory` 解析，支持绝对路径配置。

写入顺序：

1. 序列化 JSON 载荷；
2. 计算 SHA-256；
3. 写入临时载荷文件；
4. 写入临时 Manifest；
5. 原子替换正式文件；
6. 写入 Journal 备份清单；
7. 执行保留数量清理。

备份载荷和 Manifest 分离，下载或恢复准备前必须重新计算 SHA-256。

## 6. 自动备份

默认配置：

```json
{
  "TransportResilience": {
    "Enabled": true,
    "AutomaticBackupEnabled": true,
    "BackupIntervalMinutes": 60,
    "BackupRetentionCount": 48,
    "MaximumBackupAgeMinutes": 180,
    "BackupDirectory": "data/transport-backups"
  }
}
```

自动备份失败只记录 Warning，不终止 Host，也不会阻塞 PLC 轮询或调度闭环。

`RequireReadyBeforeAutomaticBackup=false` 时，即使生产就绪未通过也允许备份，Manifest 会记录 `PreflightReady=false`，便于故障现场优先保存证据。

## 7. 备份校验

校验内容：

- 文件是否存在；
- SHA-256 是否一致；
- SchemaVersion 是否支持；
- JSON 是否可解析；
- 配置标识符是否重复；
- PLC 点位映射是否引用不存在的驱动；
- 是否包含活动任务；
- 是否包含未完成命令。

活动任务和未完成命令属于 Warning，不代表备份损坏，但会明确要求人工恢复。

## 8. 恢复准备

`prepare-restore` 不直接修改运行配置，而是：

1. 校验备份；
2. 从备份提取运行配置、整定参数、站点和单轨定义；
3. 写入新的 `TransportConfigurationSnapshot`；
4. 返回人工恢复清单。

之后必须使用第十阶段现有流程：

```text
申请 ChangeConfiguration
→ 独立审批人批准
→ 使用导入的 SnapshotId 执行回滚
→ 自动生成回滚前安全快照
```

PLC 点位映射不随配置快照自动应用，原因是现场 PLC 程序版本必须人工复核。车辆位置、活动任务、路权和命令状态也不会自动写回。

## 9. 隔离恢复演练

支持场景：

- DriverOffline；
- HeartbeatTimeout；
- StateStoreUnavailable；
- OrphanReservation；
- ConfigurationVersionConflict；
- StaleConsistencyReport；
- ActiveCommandAfterRestart。

演练只复制当前状态到内存副本并评估预期处置，不修改：

- 车辆注册表；
- 执行引擎；
- 路权管理器；
- SQL 状态；
- PLC 驱动或点位。

每次演练结束都会比较车辆和路权集合，验证生产运行时未被修改。

## 10. Host API

基础路径：

```text
/api/transport/resilience
```

接口：

```text
GET  /summary
GET  /readiness
POST /readiness/run
GET  /baselines
POST /baselines
GET  /backups
POST /backups
GET  /backups/{backupId}/download
POST /backups/{backupId}/validate
POST /backups/{backupId}/prepare-restore
GET  /drills
POST /drills
GET  /report/export
```

基线创建、手动备份、下载、恢复准备、演练和报告导出要求经过认证的用户身份。

## 11. Desktop

新增菜单：

```text
生产韧性与恢复演练
```

页面提供：

- 生产就绪检查；
- 就绪检查明细；
- 运行基线查看；
- 逻辑备份清单；
- 选中备份完整性校验；
- 恢复演练历史。

Desktop 不提供：

- 逻辑备份恢复按钮；
- PLC 点位恢复按钮；
- 车辆状态写回按钮；
- 故障注入按钮。

## 12. 数据持久化

不新增数据库表。

新增 Journal 类别：

```text
ProductionReadiness = 14
OperationalBaseline = 15
LogicalBackup = 16
RecoveryDrill = 17
```

备份实体文件存放在离线文件目录，Journal 只保存 Manifest 和审计摘要。
