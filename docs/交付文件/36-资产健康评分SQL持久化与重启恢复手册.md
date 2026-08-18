# AnomalyEngine v3.4 资产健康评分 SQL 持久化与重启恢复手册

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能 | v3.4 第三阶段：资产健康评分历史持久化 |
| PR | `#27` |
| 分支 | `feature/anomaly-health-scoring-v3-4` |
| 目标分支 | `develop` |
| 当前持久化 Provider | `SqlServer` |
| 表 | `Wcs_AssetHealthScore` |
| 默认状态 | 健康评分关闭；Production 预配置 SqlServer Provider |
| 安全边界 | 只读诊断，不写 PLC、不停止设备、不改变调度 |

本文是文档 34《资产健康评分架构与运维手册》的第三阶段补充，重点说明跨重启历史、SQL 写入隔离、幂等、容量治理、分页查询和故障恢复。

## 2. 设计目标

第三阶段解决第二阶段 Memory Provider 的重启丢失问题，同时保持 WCS 实时控制路径不依赖数据库写入成功。

```text
AnomalyFusionEngine
→ AssetHealthScoringService
→ AssetHealthScoreSamplingService
→ IAssetHealthScoreHistoryStore
→ SqlSugarAssetHealthScoreHistoryStore
→ 有界 Channel
→ 独立 SqlSugarClient 批量写入
→ Wcs_AssetHealthScore
```

关键原则：

1. PLC、Fusion、任务和调度线程不执行同步 SQL 写入；
2. `RecordAsync` 只进行轻量去重、构造变化点和 Channel `TryWrite`；
3. SQL 不可用时，写入 Worker 保留当前批次并重试；
4. Channel 满时不阻塞控制路径，拒绝点位并累计 `DroppedWrites`；
5. 每批创建独立 `SqlSugarClient`，不共享并发 Client；
6. 数据库恢复后继续处理 Pending 批次；
7. 已持久化历史在 Host 重启后仍可查询；
8. SQL Provider 不替代 PLC 联锁、AlarmCenter 或设备状态快照。

## 3. 数据记录策略

系统不会按固定采样频率无条件写入数据库，只在以下情况记录：

- 资产首次出现；
- 健康等级变化；
- 健康分变化绝对值达到 `MinimumScoreChangeToRecord`；
- 距离上一记录达到 `MaximumUnchangedIntervalSeconds`，写入周期心跳点。

该策略用于控制长期表增长，同时保留异常恶化、恢复和稳定运行证据。

## 4. 数据表

`Wcs_AssetHealthScore` 主要字段：

| 字段 | 说明 |
|---|---|
| Sequence | SQL Server Identity 主键 |
| PointId | SHA-256 幂等键 |
| AssetId | 资产、车辆、设备或输送单元编号 |
| HealthScore | 当前 0～100 健康分 |
| PreviousHealthScore | 上一接受点健康分 |
| ScoreDelta | 当前减上一点 |
| Grade / PreviousGrade | 当前和上一健康等级 |
| GradeChanged | 等级是否变化 |
| Direction | Stable、Improving、Deteriorating |
| FusionRiskScore | v3.3 融合风险分 |
| FusionStatus | Normal、Observe、Warning、Alarm |
| IndependentSourceCount | 独立证据来源数量 |
| CalculatedAtUtc | 评分计算时间 |
| RecordedAtUtc | 历史记录时间 |
| Summary | 主要影响摘要 |

索引：

```text
UX_Wcs_AssetHealthScore_PointId
IX_Wcs_AssetHealthScore_AssetTime
IX_Wcs_AssetHealthScore_Time
```

`PointId` 基于 AssetId、RecordedAtUtc、CalculatedAtUtc、HealthScore 和 Grade 生成。相同变化点在重试或重启后再次提交时不会重复插入。

## 5. 配置

```json
{
  "AnomalyHealthScoring": {
    "Enabled": false,
    "HistoryProvider": "SqlServer",
    "SamplingIntervalSeconds": 10,
    "MinimumScoreChangeToRecord": 1,
    "MaximumUnchangedIntervalSeconds": 300,
    "MaximumHistoryPerAsset": 8640,
    "MaximumTrackedHistoryAssets": 10000,
    "HistoryRetentionHours": 2160,
    "TrendWindowSize": 24,
    "TrendChangeThreshold": 2,
    "MaximumHistoryQueryCount": 1000,
    "HistoryWriteChannelCapacity": 20000,
    "HistoryWriteBatchSize": 200,
    "HistoryWriteRetryDelayMs": 2000,
    "HistoryMaintenanceIntervalSeconds": 3600,
    "HistoryMaintenanceBatchSize": 2000
  }
}
```

### 5.1 参数说明

| 参数 | 说明 | 生产建议 |
|---|---|---|
| HistoryProvider | `Memory` 或 `SqlServer` | 生产跨重启历史使用 SqlServer |
| SamplingIntervalSeconds | 读取当前评分的周期 | 5～60 秒起步 |
| MinimumScoreChangeToRecord | 显著变化门槛 | 结合评分噪声整定 |
| MaximumUnchangedIntervalSeconds | 稳定状态心跳间隔 | 300～3600 秒 |
| MaximumHistoryPerAsset | 单资产最多历史点 | 与保留期、心跳频率联合计算 |
| MaximumTrackedHistoryAssets | 最大历史资产数 | 高于现场资产数并留余量 |
| HistoryRetentionHours | 时间保留期 | Production 示例 2160 小时，即 90 天 |
| MaximumHistoryQueryCount | 单次查询上限 | 防止超大响应 |
| HistoryWriteChannelCapacity | SQL 写入队列容量 | 按最长可接受数据库中断压测 |
| HistoryWriteBatchSize | 单批写入点数 | 100～1000 起步 |
| HistoryWriteRetryDelayMs | SQL 失败重试间隔 | 避免故障期间高频打库 |
| HistoryMaintenanceIntervalSeconds | 清理周期 | 通常 3600 秒 |
| HistoryMaintenanceBatchSize | 单次删除上限 | 防止大事务和锁升级 |

生产秘密只通过安全配置源提供：

```text
ConnectionStrings__WcsDb=<最小权限 SQL Server 连接串>
```

连接串不得提交到仓库。

## 6. 写入队列与守恒

状态 API：

```text
GET /api/anomaly/health/history/status
```

必须监控：

- `IsAvailable`；
- `RecordedPoints`；
- `PersistedPoints`；
- `DeduplicatedPoints`；
- `IdempotentDuplicatePoints`；
- `PendingWrites`；
- `DroppedWrites`；
- `FailedWriteBatches`；
- `LastSuccessfulWriteUtc`；
- `LastError`。

正常运行应满足：

```text
PendingWrites 长期回到 0
DroppedWrites = 0
IsAvailable = true
LastSuccessfulWriteUtc 持续更新
```

`FailedWriteBatches` 在数据库故障演练后可以大于 0，但数据库恢复后 Pending 必须归零。

当前实现是内存 Channel，不是 WAL。进程被强制终止时尚未持久化的 Pending 点可能丢失，因此：

- 关键报警生命周期仍由原异常表和 AlarmCenter 持久化；
- 健康趋势属于诊断数据，不作为安全联锁依据；
- 需要零丢失健康历史时，应在后续版本增加本地 WAL，而不是阻塞控制线程等待 SQL。

## 7. 查询 API

兼容历史接口：

```text
GET /api/anomaly/health/assets/{assetId}/history?fromUtc=&maxCount=
```

分页和时间范围：

```text
GET /api/anomaly/health/assets/{assetId}/history/page
    ?fromUtc=2026-07-01T00:00:00Z
    &toUtc=2026-07-31T23:59:59Z
    &skip=0
    &maxCount=200
```

分页从最新记录开始计算 `skip`，单页 `Items` 按时间升序返回，便于绘图。

时间范围趋势：

```text
GET /api/anomaly/health/assets/{assetId}/trend/range
    ?fromUtc=...
    &toUtc=...
    &windowSize=24
```

API 必须应用最大查询数量，不允许客户端一次拉取整张历史表。

## 8. 容量与清理

维护任务分批执行：

1. 删除超过 `HistoryRetentionHours` 的记录；
2. 删除单资产超过 `MaximumHistoryPerAsset` 的最旧记录；
3. 当资产数量超过 `MaximumTrackedHistoryAssets` 时，优先保留最近有记录的资产；
4. 每轮最多删除 `HistoryMaintenanceBatchSize`，避免大事务。

建议结合现场规模估算：

```text
预计点数 ≈ 资产数 × 每天每资产变化点/心跳点 × 保留天数
```

不要只提高上限来掩盖异常风暴或不合理心跳频率。

## 9. 启用流程

```text
保持 AnomalyHealthScoring.Enabled=false
→ 升级并初始化 Wcs_AssetHealthScore
→ 验证三个索引存在
→ 验证连接串最小权限
→ 测试环境启用 SqlServer Provider
→ 核对 IsAvailable、Pending、Dropped、FailedWriteBatches
→ 执行数据库中断与恢复演练
→ 执行 Host 重启查询验证
→ 检查表增长和清理速度
→ 审批配置版本
→ 小范围只读启用
```

不得在同一次变更中同时启用自动控制动作；v3.4 仍仅用于观察和诊断。

## 10. 故障处理

### SQL 不可用

- Host、PLC、Fusion 和调度继续运行；
- `IsAvailable=false`；
- `FailedWriteBatches` 增长；
- `PendingWrites` 可能增长；
- 数据库恢复后 Worker 自动重试。

处理顺序：

```text
确认 SQL 服务与网络
→ 检查连接串和权限
→ 观察 Pending 是否停止增长
→ 恢复 SQL
→ 等待 Pending 归零
→ 核对 DroppedWrites
→ 查询缺口时间范围
```

### Channel 满

`DroppedWrites>0` 表示健康历史完整性受影响。应调查：

- SQL 故障持续时间；
- Channel 容量；
- 资产数量；
- 变化点频率；
- 批大小和数据库吞吐。

不得通过无限增大 Channel 代替容量分析。

### 回退到 Memory

```text
AnomalyHealthScoring__HistoryProvider=Memory
→ 重启 Host
```

已有 SQL 历史保留，但 Memory Provider 不读取 SQL，重启后仅形成新的短期内存窗口。

### 完全关闭

```text
AnomalyHealthScoring__Enabled=false
→ 重启 Host
```

关闭健康评分不会影响 v3.3 Fusion、原异常检测、AlarmCenter 或调度。

## 11. 备份与恢复

`Wcs_AssetHealthScore` 应纳入 SQL Server 常规备份。恢复时：

1. 先恢复数据库；
2. 验证表和索引；
3. 保持健康评分关闭启动 Host；
4. 查询最近历史点；
5. 启用只读健康评分；
6. 验证新点继续追加且 PointId 无重复。

## 12. 安全结论

SQL Provider 只增强诊断历史的耐久性。任何健康分、趋势或历史缺口都不能单独触发设备停机、PLC 写入、任务取消、路线调整或车辆选择。安全动作仍由 PLC 联锁和经审批的控制逻辑负责。