# AnomalyEngine v3.5 资产健康事件治理与 MES 联动测试交付报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能 | v3.5 健康事件治理与 MES HTTP Outbox |
| 分支 | `feature/anomaly-health-governance-v3-5` |
| 目标分支 | `develop` |
| 专项工作流 | `WCS Asset Health Governance` |
| 默认启用状态 | false |
| 默认 MES 推送状态 | false |
| 安全边界 | 诊断通知，不进入 PLC 或调度控制 |

本文记录 v3.5 的测试范围和合并门槛。最终 Run ID、Artifact、Digest、SQL 精确计数和 Soak 指标在最终矩阵完成后补入。

## 2. 代码交付清单

| 路径 | 说明 |
|---|---|
| `AssetHealthGovernanceModels.cs` | 配置、事件、转换、状态和接口契约 |
| `AssetHealthGovernanceService.cs` | 连续计数、生命周期、确认、抑制和恢复 |
| `SqlSugarAssetHealthEventJournalStore.cs` | SQL Journal、Outbox、幂等、查询和清理 |
| `AssetHealthGovernanceEvaluationService.cs` | 周期读取 v3.4 评分并推进事件 |
| `AssetHealthMesDeliveryService.cs` | MES HTTP、幂等头、退避、DeadLetter |
| `AnomalyHealthGovernanceController.cs` | 状态、查询和治理 API |
| `AnomalyHealthGovernanceLoadController.cs` | 仅 LoadTest 的确定性评估入口 |
| `AssetHealthGovernanceServiceTests.cs` | Core 状态机单元测试 |
| `anomaly-health-governance.yml` | SQL + MES 专项 E2E |

## 3. 单元测试范围

### 3.1 创建防抖

- 第 1、2 次 Degraded 不创建事件；
- 达到连续门槛后只创建一次 Raised；
- 同一资产不重复创建活动 EventId；
- MessageId 对相同 EventId、Version 和转换保持确定性。

### 3.2 等级和峰值

- Degraded → Critical 生成 GradeChanged；
- Critical → Degraded 仍生成 GradeChanged；
- PeakGrade 保留最高等级；
- LowestHealthScore 保留最低分；
- 等级未变化时只更新内存观察值；
- 超过心跳周期生成 Observed，但不进入 MES Pending。

### 3.3 恢复防抖

- 第一次健康评估不恢复；
- 达到连续恢复门槛后生成 Recovered；
- 恢复版本沿用原 EventId；
- 恢复后活动事件列表为空；
- 再次持续异常创建新 EventId。

### 3.4 人工治理

- Acknowledge 保存操作者、时间和备注；
- 重复 Acknowledge 不重复增加版本；
- Suppress 保存操作者、原因和有效期；
- 过期时间必须晚于当前时间；
- Unsuppress 生成新版本；
- 抑制期间 GradeChanged 写 Journal 但不推送；
- Recovered 即使事件此前被抑制也允许推送。

### 3.5 重启恢复

- 最新活动事件可恢复；
- 已恢复事件保留查询但不重新激活；
- Delivered 不重新发送；
- Pending 和 Retrying 继续发送；
- 连续计数从零重新开始。

## 4. SQL Journal 验收

表：

```text
Wcs_AssetHealthEventJournal
```

验证：

- CodeFirst 可重复执行；
- MessageId 唯一索引存在；
- EventId + EventVersion 唯一索引存在；
- 同一转换重放不增加行数；
- Raised、GradeChanged、Acknowledged、Suppressed、Unsuppressed、Recovered 版本连续；
- 单事件历史按版本正序返回；
- 最新事件恢复读取正确；
- Delivered、DeadLetter 和待发送计数正确；
- 保留清理不删除 Pending 和 Retrying；
- 清理使用 TOP 批量限制。

## 5. MES E2E 验收

专项工作流启动本地 HTTP Receiver，并验证以下场景。

### 5.1 成功发送

- Raised 进入 Pending；
- Receiver 收到 JSON；
- `Idempotency-Key` 等于 MessageId；
- EventId 和 Version 与 SQL 一致；
- 2xx 后状态变为 Delivered；
- LastSuccessfulDeliveryUtc 更新。

### 5.2 幂等冲突

Receiver 对重复消息返回 409：

- WCS 将 409 视为已接收；
- 状态变为 Delivered；
- 不进入重试；
- SQL 不生成重复版本。

### 5.3 5xx 和网络中断

- Host 和 `/health/live` 保持正常；
- 消息变为 Retrying；
- AttemptCount 增加；
- NextAttemptUtc 按指数退避；
- MES 恢复后自动 Delivered；
- 不需要重启 Host。

### 5.4 DeadLetter

- 连续失败达到 MesMaximumAttempts；
- 状态变为 DeadLetter；
- 不再自动发送；
- 人工重试 API 将状态恢复为 Pending；
- 后续成功后 Delivered。

### 5.5 抑制

- Suppressed 操作本身可推送；
- 抑制期间 GradeChanged 的 DeliveryStatus=Suppressed；
- Receiver 不收到该等级变化；
- Unsuppressed 和 Recovered 按规则推送。

## 6. API 验收

验证：

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

必须确认：

- 空 ID 返回 400；
- 不存在事件返回 404；
- 缺少 Actor 或 Reason 返回 400；
- 过去的 UntilUtc 返回 400；
- 已 Delivered 消息人工重试返回冲突；
- 查询最大数量受限；
- LoadTest API 在非 LoadTest 环境返回 404。

## 7. 完整回归矩阵

最终提交必须通过：

- WCS Asset Health Governance；
- WCS Anomaly Health Scoring；
- WCS Anomaly Health Scoring SQL；
- WCS Windows CI；
- WCS End-to-End Load；
- WCS PLC Anomaly Engine Load；
- WCS PLC Anomaly Engine Soak；
- WCS Anomaly Fusion Load；
- WCS Anomaly Fusion Bridge E2E；
- WCS Transport Cycle Analysis；
- WCS PLC Telemetry Storage Load；
- WCS ML、Governance、Context Peer 和 Version Throughput；
- WCS One Hour Soak Load。

不得只用专项工作流代替完整回归。

## 8. 性能和容量门槛

必须确认：

- 没有 HTTP 请求发生在 PLC、命令、任务或调度线程；
- 评估服务单次扫描资产数有上限；
- SQL 查询和历史返回数量有上限；
- MES 每批数量有上限；
- 失败消息不会无限快速重试；
- Journal 保留期和清理批次有上限；
- 事件结束后内存状态可清理；
- 一小时 Soak 中 Pending 最终归零；
- DeadLetter 不造成 CPU 忙循环；
- Host RSS、托管内存和内部字典无持续无界增长。

## 9. 安全检查

- [ ] 默认 Enabled=false；
- [ ] 默认 MesPushEnabled=false；
- [ ] 生产配置不含 MES 密钥；
- [ ] 无 PLC 写入；
- [ ] 无设备 Stop；
- [ ] 无任务取消；
- [ ] 无路线、路权或车辆选择修改；
- [ ] MES 故障不影响控制链路；
- [ ] 查询接口无副作用；
- [ ] 写操作保存 Actor 和原因；
- [ ] 自动控制联动仍不在 v3.5 范围。

## 10. 合并门槛

- [ ] Core 状态机测试通过；
- [ ] SQL Journal 精确版本和幂等通过；
- [ ] Host 重启恢复通过；
- [ ] MES 2xx 和 409 通过；
- [ ] 5xx、超时、断线和恢复通过；
- [ ] DeadLetter 和人工重试通过；
- [ ] 抑制推送规则通过；
- [ ] 完整回归矩阵通过；
- [ ] 一小时 Soak 通过；
- [ ] 文档 00、21、39、40、41 更新；
- [ ] PR 描述包含安全边界和回退方式。

全部完成后才能将 v3.5 标记为仓库级研发完成。现场 MES 接口联调、身份权限、网络策略和投产签署仍属于项目级工作。
