# AnomalyEngine v3.4 资产健康评分 SQL 持久化测试与交付报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能 | v3.4 第三阶段：SQL 持久化与重启恢复 |
| PR | `#27` |
| 分支 | `feature/anomaly-health-scoring-v3-4` |
| 目标分支 | `develop` |
| 已验证代码基线 | `5fce78b41105d54fcfc80638f793fd7c87899290` |
| 专项工作流 | `WCS Anomaly Health Scoring SQL` |
| 默认启用状态 | `false` |
| 安全边界 | 只读诊断，不进入 PLC 或调度控制 |
| 最终验收结论 | 第三阶段代码、SQL 专项、完整回归与一小时 Soak 全部通过 |

本文与文档 36《资产健康评分 SQL 持久化与重启恢复手册》及文档 38《AnomalyEngine v3.4 生产配置与最终验收记录》共同构成 v3.4 最终交付依据。

## 2. 代码交付清单

| 路径 | 说明 |
|---|---|
| `AssetHealthScoringModels.cs` | Provider、分页、状态与 SQL 队列参数 |
| `InMemoryAssetHealthScoreHistoryStore.cs` | 分页、时间范围趋势和兼容 Memory Provider |
| `SqlSugarAssetHealthScoreHistoryStore.cs` | SQL 表、异步队列、批量幂等写入、查询和清理 |
| `AnomalyFusionDependencyInjection.cs` | Provider 选择、参数校验和 HostedService 注册 |
| `PlcMlDependencyInjection.cs` | 向 Fusion/Health DI 传递 WcsDb 连接串 |
| `DatabaseInitializer.cs` | 表和索引初始化 |
| `AnomalyHealthController.cs` | 分页和时间范围趋势 API |
| `AnomalyHealthLoadController.cs` | 仅 LoadTest 使用的持久化验证入口 |
| `appsettings.json` | Memory 安全默认和完整参数 |
| `appsettings.Production.json` | SqlServer Provider 生产预配置，功能仍默认关闭 |
| `anomaly-health-scoring-sql.yml` | SQL 生命周期专项 E2E |

## 3. 数据库交付

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

验收结果：

- CodeFirst 可重复执行；
- `PointId` 唯一索引存在；
- 相同变化点重放不增加记录；
- `AssetId + RecordedAtUtc` 使用复合查询索引；
- 时间保留清理采用受限批量删除；
- 每次 Schema、写批、查询和维护操作创建独立 `SqlSugarClient`。

## 4. 单元测试范围

### 4.1 当前评分

- disabled 返回空；
- 0～100 映射；
- 四级等级边界；
- Fusion 状态作为最低严重等级；
- 扣分因子合计与总扣分一致；
- 最差资产优先排序。

### 4.2 Memory 历史兼容

- 小变化去重；
- 心跳到期记录；
- 等级变化强制记录；
- 单资产容量上限；
- 资产数量上限；
- 时间保留；
- Stable、Improving、Deteriorating；
- 分页从最新记录计算 skip；
- 单页 Items 按时间升序；
- 时间范围趋势只使用范围内数据。

## 5. SQL 专项 E2E 最终证据

### 5.1 工作流与 Artifact

| 项目 | 结果 |
|---|---|
| 工作流 | `WCS Anomaly Health Scoring SQL #4` |
| Run ID | `30279928643` |
| 结论 | success |
| Artifact | `wcs-anomaly-health-sql-4` |
| Artifact ID | `8658434311` |
| Digest | `sha256:028ba52175063d4d2a704df029bc7f6a061bdddda60e2c361804d33ae70ec6ed` |

### 5.2 批量写入、分页和趋势

向 LoadTest API 提交 4 个确定时间的变化点，最终验证：

- `accepted=4`；
- `RecordedPoints=4`；
- `PendingWrites=0`；
- SQL 精确记录 4 条；
- `DroppedWrites=0`；
- `FailedWriteBatches=0`；
- 分页第一页返回最新两个点，`HasMore=true`；
- 趋势方向为 `Deteriorating`；
- 95 → 35，窗口变化量 `-60`，4 个样本均可查询。

### 5.3 重启恢复

停止并重新启动 Host，在不重新注入数据的情况下：

- 历史 API 仍返回 4 条；
- 顺序、等级、分数变化和时间保持一致；
- SQL Provider 状态可用；
- 历史不依赖进程内 Dictionary。

### 5.4 幂等重放

Host 重启后重新提交同一批变化点：

- `Wcs_AssetHealthScore` 行数仍为 4；
- `IdempotentDuplicatePoints=4`；
- `COUNT(*) - COUNT(DISTINCT PointId) = 0`；
- 无 duplicate key 未处理异常；
- `PendingWrites=0`。

### 5.5 SQL 中断与自动恢复

运行中停止 SQL Server 后提交 1 个变化点：

- 请求被接受，`RecordedPoints=5`；
- Host `/health/live` 保持可用；
- `IsAvailable=false`；
- `PendingWrites=1`；
- `FailedWriteBatches` 持续记录重试失败；
- `DroppedWrites=0`。

SQL Server 恢复后：

- Worker 无需重启 Host 即自动恢复；
- `IsAvailable=true`；
- `PendingWrites=0`；
- SQL 行数增加到 5；
- `PersistedPoints` 增加；
- `LastError=null`；
- `DroppedWrites=0`。

### 5.6 保留期清理

写入一个超过 1 小时保留期的旧点后执行维护：

- 维护前内存可见 6 个点；
- 维护后旧点被删除；
- `EvictedPoints=1`；
- 有效点保留；
- 最终 SQL 精确行数为 5；
- 清理采用 `HistoryMaintenanceBatchSize` 限制。

## 6. 完整回归矩阵

已验证代码基线 `5fce78b41105d54fcfc80638f793fd7c87899290` 共 16 项工作流全部成功：

| 工作流 | Run Number | Run ID | 结果 |
|---|---:|---:|---|
| WCS Anomaly Health Scoring SQL | 4 | 30279928643 | success |
| WCS Anomaly Health Scoring | 22 | 30279928612 | success |
| WCS Windows CI | 215 | 30279929053 | success |
| WCS PLC Telemetry Storage Load | 40 | 30279929025 | success |
| WCS End-to-End Load | 145 | 30279929106 | success |
| WCS PLC Anomaly Engine Load | 157 | 30279928692 | success after isolated rerun |
| WCS PLC Anomaly Engine Soak | 140 | 30279928638 | success |
| WCS Anomaly Fusion Load | 48 | 30279928554 | success |
| WCS Anomaly Fusion Bridge E2E | 40 | 30279928762 | success |
| WCS Transport Cycle Analysis | 43 | 30279928964 | success |
| WCS PLC Anomaly ML | 100 | 30279928555 | success |
| WCS PLC Anomaly ML E2E | 92 | 30279928931 | success |
| WCS PLC Anomaly ML Version Throughput | 68 | 30279928877 | success |
| WCS PLC Anomaly ML Governance | 53 | 30279928552 | success |
| WCS PLC Anomaly ML Context Peer | 41 | 30279928533 | success |
| WCS One Hour Soak Load | 111 | 30279928736 | success |

Anomaly Engine Load 首次执行的业务计数与 SQL 生命周期已通过，仅 Linux Runner 进程 RSS 回收门槛未满足；在未修改代码、未删除测试、未放宽门槛的情况下对同一 Job 进行独立复验并成功。

## 7. Anomaly Engine Load 复验指标

| 指标 | 结果 |
|---|---:|
| 总事件 | 213,000 |
| 处理速率 | 20,032.69 events/s |
| Raised / Recovered | 3,000 / 3,000 |
| Failures / Suppressed | 0 / 0 |
| ActiveAnomalies | 0 |
| 初始 RSS | 157.81 MB |
| 最终 RSS | 296.36 MB |
| 最终增长 | 138 MB |
| Artifact | `wcs-plc-anomaly-157` |
| Artifact ID | `8670045188` |
| Digest | `sha256:8c86f105b88b160d90e1bc4b56dee8b2aa259775305b02f5ccb7988464b4ee83` |

## 8. 一小时 Soak 最终指标

| 指标 | 结果 |
|---|---:|
| Run | `WCS One Hour Soak Load #111` |
| Run ID | `30279928736` |
| 总请求 | 18,331,511 |
| 失败请求 | 0 |
| 吞吐 | 5,091.53 RPS |
| P50 / P95 / P99 | 4 / 8 / 25 ms |
| SignalR 连接 | 100 / 100 |
| SignalR 消息 | 2,829,000 |
| SignalR 错误 / 异常关闭 | 0 / 0 |
| 初始 / 最终 / 峰值 RSS | 162.86 / 357.51 / 516.32 MB |
| Q4-Q2 | 33.49 MB |
| 最后 15 分钟斜率 | 0.432 MB/min |
| Full GC 后托管内存 | 44.65 MB |
| Endpoint Post-GC RSS | 316.77 MB |
| 任务队列峰值 / 最终值 | 2 / 0 |
| 持久化设备数 | 18 |
| 持久化任务数 | 4,768 |
| SQL device_state_logs / task_runs | 1,133 / 4,771 |
| Artifact | `wcs-one-hour-soak-111` |
| Artifact ID | `8660305875` |
| Digest | `sha256:f07d89b1eef5ebc3b7f02d428060a46ebc2577c32fdda7a77d227cb9718a8989` |

## 9. 性能和容量验收

已确认：

- `RecordAsync` 不等待 SQL；
- Channel 容量有上限；
- 每批创建独立 `SqlSugarClient`；
- 批次失败时保留当前批并重试；
- 数据库恢复后 Pending 归零；
- 正常和故障恢复场景 `DroppedWrites=0`；
- 状态 API 明确暴露数据库不可用和最后错误；
- 分页查询受 `MaximumHistoryQueryCount` 限制；
- 清理受 `HistoryMaintenanceBatchSize` 限制。

## 10. 已知边界

当前第三阶段不包含本地 WAL。以下场景可能损失尚未持久化的 Pending 点：

- Host 进程被强制结束；
- 操作系统崩溃；
- 机器断电；
- Channel 满后新点被拒绝。

该边界已在文档 36 中明确。健康历史不是安全联锁依据，不能以同步 SQL 阻塞控制线程来换取诊断数据零丢失。

## 11. 合并门槛

- [x] 专项 Core 测试通过；
- [x] SQL 批量写入通过；
- [x] 重启恢复通过；
- [x] 幂等重放通过；
- [x] SQL 中断隔离与自动恢复通过；
- [x] 保留期清理通过；
- [x] 精确 SQL 行数通过；
- [x] 完整回归矩阵通过；
- [x] 一小时 Soak 通过；
- [x] Production 配置保持 `Enabled=false`；
- [x] 文档 00、36、37、38 和 PR 描述已更新；
- [x] 未接入 PLC 写入、自动停机或调度决策。

**第三阶段软件研发与 CI 验收完成，可进入 PR Ready 和合并流程。**