# AnomalyEngine v3.4 资产健康评分 SQL 持久化测试与交付报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能 | v3.4 第三阶段：SQL 持久化与重启恢复 |
| PR | `#27` |
| 分支 | `feature/anomaly-health-scoring-v3-4` |
| 目标分支 | `develop` |
| 专项工作流 | `WCS Anomaly Health Scoring SQL` |
| 默认启用状态 | false |
| 安全边界 | 只读诊断，不进入 PLC 或调度控制 |

本文与文档 36《资产健康评分 SQL 持久化与重启恢复手册》共同构成第三阶段交付依据。最终运行号、Artifact 和指标在最新矩阵完成后补入最终验收记录。

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

验收要求：

- CodeFirst 可重复执行；
- PointId 唯一索引存在；
- 相同变化点重放不增加记录；
- AssetId + RecordedAtUtc 查询使用复合索引；
- 时间保留清理不执行全表单次删除。

## 4. 单元测试范围

### 当前评分

- disabled 返回空；
- 0～100 映射；
- 四级等级边界；
- Fusion 状态作为最低严重等级；
- 扣分因子合计与总扣分一致；
- 最差资产优先排序。

### Memory 历史兼容

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

## 5. SQL 专项 E2E

工作流：

```text
.github/workflows/anomaly-health-scoring-sql.yml
```

### 5.1 构建与基础测试

- 构建 Core.Tests；
- 运行 AssetHealthScoringServiceTests；
- 运行 AssetHealthScoreHistoryStoreTests；
- 运行 AssetHealthHistoryPagingTests；
- 构建 Wcs.Host。

### 5.2 批量写入

向 LoadTest API 提交 4 个确定时间的变化点，验证：

- `accepted=4`；
- `PendingWrites` 最终为 0；
- SQL 精确记录 4 条；
- `DroppedWrites=0`；
- 分页第一页返回最新两个点；
- 趋势方向为 Deteriorating。

### 5.3 重启恢复

停止并重新启动 Host，在不重新注入数据的情况下验证：

- 历史 API 仍返回 4 条；
- SQL Provider 状态可用；
- 历史不依赖进程内 Dictionary。

### 5.4 幂等重放

Host 重启后重新提交同一批变化点，验证：

- `Wcs_AssetHealthScore` 行数仍为 4；
- `IdempotentDuplicatePoints >= 4`；
- `COUNT(*) - COUNT(DISTINCT PointId) = 0`；
- 不出现 duplicate key 异常。

### 5.5 SQL 中断与恢复

运行中停止 SQL Server，再提交一个变化点，验证：

- LoadTest 请求仍被快速接受；
- Host `/health/live` 继续成功；
- `IsAvailable=false`；
- `PendingWrites >= 1`；
- `FailedWriteBatches >= 1`。

恢复 SQL Server 后验证：

- Worker 自动重试；
- `PendingWrites=0`；
- SQL 行数增加到 5；
- `DroppedWrites=0`；
- 无需重启 Host。

### 5.6 保留期清理

写入一个超过保留期的旧点，执行维护后验证：

- 旧点被删除；
- 有效点保留；
- `EvictedPoints >= 1`；
- 最终 SQL 精确行数为 5；
- 清理使用 TOP 批量限制。

## 6. 完整回归矩阵

第三阶段最终提交必须同时通过：

- WCS Anomaly Health Scoring；
- WCS Anomaly Health Scoring SQL；
- WCS Windows CI；
- WCS End-to-End Load；
- WCS PLC Anomaly Engine Load；
- WCS PLC Anomaly Engine Soak；
- WCS Anomaly Fusion Load；
- WCS Anomaly Fusion Bridge E2E；
- WCS Transport Cycle Analysis；
- WCS One Hour Soak Load。

不得只用 SQL 专项工作流代替完整回归。

## 7. 性能和容量验收

必须确认：

- RecordAsync 不等待 SQL；
- Channel 容量有上限；
- 每批创建独立 SqlSugarClient；
- 批次失败时保留当前批并重试；
- 数据库恢复后 Pending 归零；
- 正常场景 DroppedWrites 为 0；
- 状态 API 不隐瞒数据库不可用；
- 分页查询受 MaximumHistoryQueryCount 限制；
- 清理受 HistoryMaintenanceBatchSize 限制。

## 8. 已知边界

当前第三阶段不包含本地 WAL。以下场景可能损失未持久化 Pending 点：

- Host 进程被强制结束；
- 操作系统崩溃；
- 机器断电；
- Channel 满后新点被拒绝。

该边界已在文档 36 中明确。健康历史不是安全联锁依据，不能以同步 SQL 阻塞控制线程来换取诊断数据零丢失。

## 9. 合并门槛

- [ ] 专项 Core 测试通过；
- [ ] SQL 批量写入通过；
- [ ] 重启恢复通过；
- [ ] 幂等重放通过；
- [ ] SQL 中断隔离与自动恢复通过；
- [ ] 保留期清理通过；
- [ ] 精确 SQL 行数通过；
- [ ] 完整回归矩阵通过；
- [ ] 一小时 Soak 通过；
- [ ] Production 配置保持 Enabled=false；
- [ ] 文档 00、36、37 和 PR 描述已更新；
- [ ] 未接入 PLC 写入、自动停机或调度决策。

上述项目全部完成后，第三阶段才能签署完成。