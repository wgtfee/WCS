# AnomalyEngine v3.5 资产健康事件治理与 MES 联动测试交付报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能版本 | AnomalyEngine v3.5 — 健康事件治理与 MES HTTP Outbox |
| PR | `#28` |
| 分支 | `feature/anomaly-health-governance-v3-5` |
| 目标分支 | `develop` |
| 已验证代码基线 | `1315ba7108da545222252747fff00072f19a0219` |
| 默认治理状态 | `Enabled=false` |
| 默认 MES 推送状态 | `MesPushEnabled=false` |
| 安全边界 | 诊断与通知，不写 PLC、不停机、不取消任务、不改变路线、路权、车辆选择或调度 |

## 2. 交付能力

v3.5 在 v3.4 当前评分和 SQL 历史之上提供：

- 持续低健康分转为正式健康事件；
- 连续异常和连续恢复防抖；
- 同一资产同时只保留一个活动事件；
- Raised、Observed、GradeChanged、Acknowledged、Suppressed、Unsuppressed、Recovered 生命周期；
- PeakGrade、LowestHealthScore、Actor、Reason、Note 审计；
- SQL Server 不可变 Journal；
- 确定性 SHA-256 MessageId 与 EventId + Version 幂等；
- MES HTTP Outbox、超时、指数退避、DeadLetter 和人工重放；
- HTTP 2xx 与 409 幂等成功语义；
- Host 重启后活动事件和待发送状态恢复；
- 状态、事件、历史、确认、抑制、解除和重试 API。

## 3. 代码与工作流清单

| 交付物 | 说明 |
|---|---|
| `AssetHealthGovernanceModels.cs` | 配置、事件、转换、状态与接口 |
| `AssetHealthGovernanceService.cs` | 生命周期、防抖、确认、抑制与恢复 |
| `SqlSugarAssetHealthEventJournalStore.cs` | SQL Journal、Outbox、幂等、查询与清理 |
| `AssetHealthGovernanceEvaluationService.cs` | 周期读取 v3.4 当前评分 |
| `AssetHealthMesDeliveryService.cs` | MES HTTP、幂等头、退避与 DeadLetter |
| `AnomalyHealthGovernanceController.cs` | 生产治理 API |
| `AnomalyHealthGovernanceLoadController.cs` | 仅 LoadTest 的确定性入口 |
| `AssetHealthGovernanceServiceTests.cs` | Core 状态机单元测试 |
| `anomaly-health-governance.yml` | SQL + MES + 重启专项 E2E |
| `anomaly-health-governance-compile.yml` | 保留编译诊断 Artifact |

## 4. Core 单元测试

已验证：

- 未达到连续门槛不创建事件；
- 达到门槛只生成一个 Raised；
- 同一资产不重复产生活动 EventId；
- Degraded 与 Critical 等级变化生成连续版本；
- PeakGrade 和 LowestHealthScore 正确保留；
- 不变心跳生成 Observed，但不进入 MES Pending；
- 连续恢复达到门槛后生成 Recovered；
- 恢复后再次异常生成新 EventId；
- Acknowledge 保存操作者、时间和备注；
- 重复确认不增加版本；
- Suppress/Unsuppress 保存原因和有效期；
- 抑制期间 GradeChanged 写 Journal 但不推送；
- Recovered 即使此前被抑制仍可推送；
- Restore 恢复活动事件，不重复投递 Delivered 消息。

## 5. SQL Journal 验收

表：

```text
Wcs_AssetHealthEventJournal
```

索引与约束：

- MessageId 唯一；
- EventId + EventVersion 唯一；
- EventId + Version 正序查询；
- DeliveryStatus + NextDeliveryAttemptUtc 支持 Outbox；
- EventRetentionHours 与 MaintenanceBatchSize 控制清理；
- Pending、Retrying 不被保留期清理；
- 所有可空时间和备注字段显式 `IsNullable=true`。

专项 E2E 已验证 SQL 版本连续、重放不增加行数、投递状态精确、活动事件可跨 Host 重启恢复。

## 6. MES Outbox 验收

### 6.1 成功与幂等

- Raised 进入 Pending；
- Receiver 收到 JSON；
- `Idempotency-Key` 等于 MessageId；
- EventId 和 Version 与 SQL 一致；
- HTTP 2xx 后变为 Delivered；
- HTTP 409 视为幂等成功，不进入重试。

### 6.2 故障与恢复

- HTTP 5xx、超时和断线不影响 Host `/health/live`；
- 状态进入 Retrying；
- AttemptCount 增加；
- NextDeliveryAttemptUtc 使用指数退避；
- MES 恢复后自动 Delivered；
- 达到最大尝试次数进入 DeadLetter；
- 人工重试恢复为 Pending，并可最终 Delivered。

### 6.3 抑制语义

- Suppressed 操作可通知 MES；
- 抑制期间等级变化仍进入 SQL Journal，但 DeliveryStatus=Suppressed；
- Receiver 不收到被抑制的 GradeChanged；
- Unsuppressed 与 Recovered 按规则发送。

## 7. 专项工作流证据

`WCS Asset Health Governance #9` 全部步骤成功：

- Build Host and focused tests；
- Prepare SQL Server；
- Start deterministic MES receiver；
- Start Host；
- Verify raised, acknowledge, suppress, and unsuppress；
- Verify retry, dead letter, manual replay, and recovery；
- Verify SQL versions, idempotency, and receiver contract；
- Verify Host restart recovery。

证据：

| 项目 | 值 |
|---|---|
| Workflow Run | `WCS Asset Health Governance #9` |
| Run ID | `30320836736` |
| Artifact | `wcs-asset-health-governance-9` |
| Digest | `sha256:3b558cfe7875004cf85c61a9dd0f35774d4df915f6c0efe3a600d2a49a852a4c` |
| 结果 | Success |

## 8. 源代码完整回归矩阵

代码基线 `1315ba7108da545222252747fff00072f19a0219` 共 13 项全部成功：

| 工作流 | 运行号 | 结果 |
|---|---:|---|
| WCS Asset Health Governance | 9 | Success |
| WCS Asset Health Governance Compile | 4 | Success |
| WCS Anomaly Health Scoring | 34 | Success |
| WCS Anomaly Health Scoring SQL | 16 | Success |
| WCS Windows CI | 228 | Success |
| WCS End-to-End Load | 157 | Success |
| WCS PLC Telemetry Storage Load | 52 | Success |
| WCS PLC Anomaly Engine Load | 169 | Success |
| WCS PLC Anomaly Engine Soak | 152 | Success |
| WCS Anomaly Fusion Load | 60 | Success |
| WCS Anomaly Fusion Bridge E2E | 52 | Success |
| WCS Transport Cycle Analysis | 55 | Success |
| WCS One Hour Soak Load | 123 | Success |

专项工作流不能代替完整回归；以上矩阵同时覆盖原有调度、Telemetry、异常、Fusion、健康评分、SQL 历史与持续运行链路。

## 9. API 验收

已覆盖：

```http
GET  /api/anomaly/health-governance/status
GET  /api/anomaly/health-governance/events
GET  /api/anomaly/health-governance/events/{eventId}
GET  /api/anomaly/health-governance/events/{eventId}/history
POST /api/anomaly/health-governance/events/{eventId}/acknowledge
POST /api/anomaly/health-governance/events/{eventId}/suppress
POST /api/anomaly/health-governance/events/{eventId}/unsuppress
POST /api/anomaly/health-governance/deliveries/{messageId}/retry
```

生产要求：治理写操作必须接入项目身份授权，Actor 与原因不可由匿名客户端随意伪造。LoadTest API 在非 LoadTest 环境必须返回 404。

## 10. 性能、容量与安全

- HTTP 不在 PLC、命令、任务或调度线程执行；
- 单次扫描、查询、MES 批量和 SQL 清理均有上限；
- 失败消息使用有界退避，不产生 CPU 忙循环；
- DeadLetter 不再自动发送；
- Journal 和内存状态具有保留期；
- 一小时 Soak 已成功；
- 仓库生产默认 `Enabled=false`；
- 仓库生产默认 `MesPushEnabled=false`；
- Git 不保存生产 MES 密钥；
- 无 PLC 写入、Stop、任务取消或调度修改。

## 11. 回退

优先关闭 MES 推送：

```text
AssetHealthGovernance__MesPushEnabled=false
```

完全关闭事件治理：

```text
AssetHealthGovernance__Enabled=false
```

关闭 v3.5 不影响 PLC、任务、调度、Fusion、v3.4 当前评分或健康历史查询。SQL Journal 应保留用于审计，不应作为紧急回退的一部分直接删除。

## 12. 仓库级结论

- [x] Core 状态机测试通过；
- [x] SQL Journal 精确版本和幂等通过；
- [x] Host 重启恢复通过；
- [x] MES 2xx、409、5xx、DeadLetter 和人工重试通过；
- [x] 抑制推送规则通过；
- [x] 源代码完整回归矩阵通过；
- [x] 一小时 Soak 通过；
- [x] 默认关闭与控制安全边界通过；
- [x] 文档 00、21、39、40、41、42 已补齐。

**v3.5 仓库级软件研发与自动化验收完成。现场 MES 真实接口、身份权限、网络策略、阈值和投产签署仍属于独立项目级验收。**
