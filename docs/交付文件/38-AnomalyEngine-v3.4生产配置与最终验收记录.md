# AnomalyEngine v3.4 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能版本 | AnomalyEngine v3.4 — 可解释资产健康评分、趋势与 SQL 历史 |
| PR | `#27` |
| 研发分支 | `feature/anomaly-health-scoring-v3-4` |
| 目标分支 | `develop` |
| 已验证代码基线 | `5fce78b41105d54fcfc80638f793fd7c87899290` |
| 软件验收状态 | 通过 |
| 现场投产状态 | 未声明，仍需项目级联调与签署 |
| 安全边界 | 只读诊断，不写 PLC、不停机、不改变任务、路径、路权或调度决策 |

## 2. v3.4 最终交付能力

v3.4 在 v3.3 多模型异常证据融合之上提供：

- 0～100 资产健康分；
- Healthy、Attention、Degraded、Critical 四级健康状态；
- 评分扣分因子、来源和原因解释；
- 首次、等级变化、显著变化和周期心跳历史；
- Stable、Improving、Deteriorating 趋势及每小时斜率；
- Memory 和 SqlServer 两种历史 Provider；
- SQL 异步批量幂等写入；
- SQL 中断隔离和自动恢复；
- Host 重启后的跨进程历史查询；
- 时间范围、分页和趋势只读 API；
- 保留期、单资产容量、资产数量和清理批次治理；
- Provider 可用性、Pending、Persisted、Duplicate、Dropped、FailedBatch 和 LastError 指标。

## 3. 生产安全默认值

`appsettings.Production.json` 的设计原则：

```json
{
  "AnomalyHealthScoring": {
    "Enabled": false,
    "HistoryProvider": "SqlServer"
  }
}
```

含义：

- 生产文件预配置 SQL Provider，但功能默认关闭；
- 未经现场阈值、容量和数据库评审，不自动启用；
- Git 中不保存生产数据库密码、Token 或现场点位；
- Simulator、异常检测、ML、周期分析、Fusion 和健康评分均由现场变更流程分别启用；
- 健康分只用于展示、查询、趋势和诊断。

基础 `appsettings.json` 保持：

```json
{
  "AnomalyHealthScoring": {
    "Enabled": false,
    "HistoryProvider": "Memory"
  }
}
```

因此开发或未显式配置的部署不会自动创建健康历史 SQL 写入负载。

## 4. 必须外部注入的生产参数

至少必须通过环境变量、安全配置中心或部署平台提供：

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__WcsDb=<生产 SQL Server 最小权限连接字符串>
```

启用健康评分时，还需显式确认或覆盖：

```text
AnomalyHealthScoring__Enabled=true
AnomalyHealthScoring__HistoryProvider=SqlServer
AnomalyHealthScoring__SqlChannelCapacity=...
AnomalyHealthScoring__SqlBatchSize=...
AnomalyHealthScoring__SqlFlushIntervalMs=...
AnomalyHealthScoring__SqlRetryDelayMs=...
AnomalyHealthScoring__HistoryRetentionHours=...
AnomalyHealthScoring__HistoryMaintenanceBatchSize=...
AnomalyHealthScoring__MaximumHistoryQueryCount=...
```

生产连接账号应遵循最小权限原则，禁止使用开发密码或将凭据提交到仓库。

## 5. 数据库对象

表：

```text
Wcs_AssetHealthScore
```

索引：

```text
UX_Wcs_AssetHealthScore_PointId
IX_Wcs_AssetHealthScore_AssetTime
IX_Wcs_AssetHealthScore_Time
```

上线前必须确认：

- 表和索引创建成功；
- SQL Server 磁盘和日志空间满足保留期容量；
- 数据库备份策略覆盖该表；
- 清理批次不会造成长事务；
- 查询账号和应用账号权限经过审批。

## 6. 最终 CI 验收

已验证代码基线共 16 项工作流全部成功：

- WCS Anomaly Health Scoring SQL #4；
- WCS Anomaly Health Scoring #22；
- WCS Windows CI #215；
- WCS PLC Telemetry Storage Load #40；
- WCS End-to-End Load #145；
- WCS PLC Anomaly Engine Load #157；
- WCS PLC Anomaly Engine Soak #140；
- WCS Anomaly Fusion Load #48；
- WCS Anomaly Fusion Bridge E2E #40；
- WCS Transport Cycle Analysis #43；
- WCS PLC Anomaly ML #100；
- WCS PLC Anomaly ML E2E #92；
- WCS PLC Anomaly ML Version Throughput #68；
- WCS PLC Anomaly ML Governance #53；
- WCS PLC Anomaly ML Context Peer #41；
- WCS One Hour Soak Load #111。

完整 Run ID、Artifact、Digest 和指标见文档 37。

## 7. SQL 专项验收摘要

| 项目 | 结果 |
|---|---|
| 批量变化点 | 4 条全部接受并持久化 |
| 重启恢复 | Host 重启后仍查询到 4 条 |
| 幂等重放 | 重放 4 条，SQL 行数不增加，重复计数为 4 |
| SQL 中断 | Host 保持存活，Pending=1，Dropped=0 |
| SQL 恢复 | 自动补写，Pending=0，无需重启 Host |
| 保留期 | 旧点清理，EvictedPoints=1，最终有效行数 5 |
| Artifact | `wcs-anomaly-health-sql-4` |
| Digest | `sha256:028ba52175063d4d2a704df029bc7f6a061bdddda60e2c361804d33ae70ec6ed` |

## 8. 一小时 Soak 摘要

| 指标 | 结果 |
|---|---:|
| 请求数 | 18,331,511 |
| 失败 | 0 |
| 吞吐 | 5,091.53 RPS |
| P95 | 8 ms |
| SignalR 消息 | 2,829,000 |
| SignalR 错误 | 0 |
| 初始 / 最终 / 峰值 RSS | 162.86 / 357.51 / 516.32 MB |
| 最后 15 分钟斜率 | 0.432 MB/min |
| Full GC 后托管内存 | 44.65 MB |
| 队列峰值 / 最终值 | 2 / 0 |
| Artifact | `wcs-one-hour-soak-111` |
| Digest | `sha256:f07d89b1eef5ebc3b7f02d428060a46ebc2577c32fdda7a77d227cb9718a8989` |

## 9. 部署前检查清单

- [ ] 已使用 Production 环境；
- [ ] 已通过安全方式注入 `ConnectionStrings__WcsDb`；
- [ ] 已确认 `Simulator:Enabled=false`；
- [ ] 已确认基础异常、ML、周期和 Fusion 配置；
- [ ] 已评审健康分阈值和等级含义；
- [ ] 已评审 SQL Channel、Batch、Retry 和保留期容量；
- [ ] 已确认表、索引、磁盘、日志和备份；
- [ ] 已确认 `AnomalyHealthScoring:Enabled` 的变更审批；
- [ ] 已验证状态 API 中 `IsAvailable=true`、`PendingWrites=0`、`DroppedWrites=0`；
- [ ] 已完成现场只读观察期；
- [ ] 已明确健康评分不得替代 PLC 联锁和 AlarmCenter。

## 10. 上线观察与告警建议

启用后持续监控：

- `IsAvailable`；
- `PendingWrites`；
- `PersistedPoints`；
- `IdempotentDuplicatePoints`；
- `DroppedWrites`；
- `FailedWriteBatches`；
- `LastSuccessfulWriteUtc`；
- `LastError`；
- SQL 表行数、数据文件和事务日志增长；
- API 查询时延；
- Host CPU、托管堆和 RSS。

建议在只读观察期内保持所有自动控制联动关闭。

## 11. 回退方案

### 11.1 关闭健康评分

```text
AnomalyHealthScoring__Enabled=false
```

重启 Host 后停止评分采样和历史写入，不影响 PLC、任务、调度和原异常生命周期。

### 11.2 回退到 Memory Provider

```text
AnomalyHealthScoring__HistoryProvider=Memory
```

适用于 SQL Provider 故障隔离或现场临时诊断。回退后新历史只保存在内存，Host 重启会丢失。

### 11.3 版本回退

回退到 v3.3 或合并前版本时：

- `Wcs_AssetHealthScore` 可保留，不影响旧版本运行；
- 不应在未备份情况下删除历史表；
- 回退后验证 Host、PLC 轮询、EventBus、AlarmCenter、任务与调度链路；
- 保留本次变更、回退原因和现场签署记录。

## 12. 已知边界

当前 SQL 异步队列为内存 Channel，不包含本地 WAL。Host 被强制终止、操作系统崩溃、断电或 Channel 满时，尚未落库的诊断点可能丢失。

该边界不影响安全控制，因为健康评分历史不是 PLC 联锁、设备停止或调度决策依据。后续只有在明确需要诊断数据掉电零丢失时，才应单独设计 WAL 阶段并重新进行容量、恢复和故障测试。

## 13. 最终结论

- [x] v3.4 当前评分、解释、历史和趋势完成；
- [x] SQL 持久化、分页和重启恢复完成；
- [x] 幂等、中断、恢复和保留期测试完成；
- [x] 16 项完整 CI 全部成功；
- [x] 一小时 Soak 成功；
- [x] 生产配置默认关闭且无仓库明文凭据；
- [x] 文档 00、21、34、35、36、37、38 完成；
- [x] 未接入 PLC 写入、自动停机、任务取消、路线或调度决策。

**AnomalyEngine v3.4 软件研发与仓库级验收完成，可将 PR #27 标记为 Ready 并合入 `develop`。**