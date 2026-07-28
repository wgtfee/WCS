# AnomalyEngine v3.5 资产健康事件治理与 MES 联动架构运维手册

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能名称 | AnomalyEngine v3.5 — 健康事件治理与 MES 联动 |
| 研发分支 | `feature/anomaly-health-governance-v3-5` |
| 目标分支 | `develop` |
| 上游依赖 | v3.4 可解释健康评分、趋势与 SQL 历史 |
| 默认状态 | `AssetHealthGovernance:Enabled=false` |
| MES 默认状态 | `AssetHealthGovernance:MesPushEnabled=false` |
| 安全边界 | 诊断与通知，不写 PLC、不停机、不改变任务、路径、路权或调度 |

## 2. 目标

v3.5 解决“健康分已经计算出来，但没有正式业务生命周期、确认、抑制、恢复和可靠通知”的问题。

输出能力：

- 持续低健康分生成正式事件；
- 等级变化和恢复采用连续计数防抖；
- 同一资产一个活动健康事件；
- 事件确认、抑制、解除和恢复；
- 不可变 SQL Journal；
- MES HTTP Outbox；
- 幂等、重试、DeadLetter 和人工重试；
- Host 重启恢复活动事件和待推送消息；
- 只读查询与受控治理 API。

## 3. 不包含的能力

- 自动停机；
- 自动取消任务或重新调度；
- 自动释放路权；
- PLC 写入；
- 替代 AlarmCenter；
- 根因图和传播分析；
- 自动生成维修结论；
- 剩余寿命预测。

后四项分别属于 v3.6～v3.9 路线，见文档 39。

## 4. 总体数据流

```text
AnomalyFusion v3.3
      ↓
AssetHealthScoring v3.4
      ↓ 只读评分快照
AssetHealthGovernanceEvaluationService
      ↓
AssetHealthGovernanceService
      ├── 连续异常/恢复计数
      ├── Raised / GradeChanged / Recovered
      ├── Acknowledged / Suppressed / Unsuppressed
      └── 活动事件内存快照
      ↓
IAssetHealthEventJournalStore
      ↓
Wcs_AssetHealthEventJournal
      ├── 不可变事件版本
      └── MES Outbox 状态
      ↓
AssetHealthMesDeliveryService
      ↓ HTTP + Idempotency-Key
MES
```

评估、SQL 和 HTTP 均位于诊断后台服务，不在 PLC 轮询、设备命令、任务状态机和调度线程内执行。

## 5. 事件生成规则

默认最低事件等级：

```text
MinimumEventGrade = Degraded
```

默认创建条件：

```text
连续 3 次评分 >= Degraded
```

默认恢复条件：

```text
连续 3 次评分 < Degraded
```

评分周期默认为 10 秒，因此默认情况下需要约 30 秒持续异常才创建事件，约 30 秒持续恢复才结束事件。

事件键：

```text
EventKey = AssetId
```

同一资产同时只允许一个活动健康事件。恢复后再次持续异常将创建新的 EventId。

## 6. 事件状态与转换

### 6.1 生命周期

```text
无活动事件
  → Raised
  → Active
  → Recovered
```

### 6.2 审计转换

```text
Raised
Observed
GradeChanged
Acknowledged
Suppressed
Unsuppressed
Recovered
```

`Observed` 是活动事件周期心跳，默认每 300 秒最多记录一次，不发送 MES，避免无变化消息造成通知风暴。

### 6.3 等级变化

活动事件仍处于阈值以上时：

- Degraded → Critical：记录 `GradeChanged`；
- Critical → Degraded：仍记录 `GradeChanged`；
- PeakGrade 保留生命周期内最高严重等级；
- LowestHealthScore 保留生命周期内最低健康分。

## 7. 确认与抑制

### 7.1 确认

确认只表示人员已经看到并开始处理，不改变事件是否活动，也不影响健康评分。

保存：

- Acknowledged；
- AcknowledgedAtUtc；
- AcknowledgedBy；
- 操作备注；
- 事件版本。

### 7.2 抑制

抑制适用于已知测试、计划检修或已确认噪声窗口。

保存：

- IsSuppressed；
- SuppressedUntilUtc；
- SuppressedReason；
- 操作者；
- 操作时间。

抑制期间等级变化仍写入 SQL Journal，但该等级变化消息标记为 `Suppressed`，不发送 MES。恢复消息仍发送，用于关闭上层事件。

抑制到期后后台维护自动生成 `Unsuppressed` 转换。

## 8. SQL Journal 与 Outbox

表：

```text
Wcs_AssetHealthEventJournal
```

每个转换保存完整事件快照，不覆盖历史版本。

幂等约束：

```text
MessageId = SHA256(EventId + EventVersion + TransitionType)
唯一索引：MessageId
唯一索引：EventId + EventVersion
```

主要索引：

```text
UX_Wcs_AssetHealthEventJournal_MessageId
UX_Wcs_AssetHealthEventJournal_EventVersion
IX_Wcs_AssetHealthEventJournal_AssetTime
IX_Wcs_AssetHealthEventJournal_Delivery
```

Outbox 状态：

```text
Disabled
Pending
Retrying
Delivered
Suppressed
DeadLetter
```

`Disabled` 和 `Suppressed` 不进入发送查询。

## 9. MES HTTP 契约

默认：

```http
POST {MesBaseUrl}/api/wcs/asset-health-events
Idempotency-Key: {MessageId}
X-WCS-Event-Id: {EventId}
X-WCS-Event-Version: {Version}
Content-Type: application/json
```

载荷包含：

- MessageId；
- EventId、EventVersion；
- Transition；
- AssetId、EventKey；
- LifecycleStatus；
- Grade、PeakGrade；
- HealthScore、LowestHealthScore；
- 首次、最近和恢复时间；
- 确认与抑制信息；
- Reason、Source、Category；
- Actor、Note；
- SourceSystem=WCS。

MES 必须按 `Idempotency-Key` 去重。

成功口径：

- 任意 2xx；
- 409 Conflict，表示 MES 已接收相同幂等消息。

## 10. 重试策略

默认：

| 参数 | 默认值 |
|---|---:|
| MesTimeoutSeconds | 5 |
| MesPollIntervalSeconds | 2 |
| MesBatchSize | 100 |
| MesMaximumAttempts | 10 |
| MesInitialRetrySeconds | 5 |
| MesMaximumRetrySeconds | 300 |

退避：

```text
RetryDelay = min(MaximumRetry, InitialRetry × 2^(Attempt-1))
```

达到最大次数后进入 DeadLetter，不再自动发送。运维人员排除问题后可调用人工重试 API。

## 11. 重启恢复

Host 启动：

1. 初始化 Journal 表和索引；
2. 读取每个 EventId 最新版本；
3. 恢复活动事件内存快照；
4. 不重新发送 Delivered 消息；
5. Pending 和 Retrying 消息继续发送；
6. DeadLetter 保持等待人工处理；
7. 连续异常和恢复计数从零重新积累。

连续计数不跨重启恢复是安全选择，避免重启后凭旧计数立即创建或恢复事件。

## 12. API

基础路径：

```text
/api/anomaly/health-governance
```

接口：

```http
GET  /status
GET  /events
GET  /events/{eventId}
GET  /events/{eventId}/history
POST /events/{eventId}/acknowledge
POST /events/{eventId}/suppress
POST /events/{eventId}/unsuppress
POST /deliveries/{messageId}/retry
```

写请求优先使用已认证身份名称；未接入身份系统的环境必须显式提供 Actor。生产部署应在网关或 Host 身份体系中限制治理写接口，不允许匿名公网访问。

## 13. 配置

```json
{
  "AssetHealthGovernance": {
    "Enabled": false,
    "MinimumEventGrade": "Degraded",
    "ConsecutiveUnhealthyEvaluations": 3,
    "ConsecutiveRecoveryEvaluations": 3,
    "EvaluationIntervalSeconds": 10,
    "MaximumUnchangedEventIntervalSeconds": 300,
    "MaximumTrackedAssets": 10000,
    "MaximumEventsQueryCount": 1000,
    "InactiveStateRetentionSeconds": 86400,
    "EventRetentionHours": 2160,
    "MaintenanceIntervalSeconds": 3600,
    "MaintenanceBatchSize": 2000,
    "MesPushEnabled": false,
    "MesBaseUrl": "",
    "MesEndpointPath": "/api/wcs/asset-health-events",
    "MesTimeoutSeconds": 5,
    "MesPollIntervalSeconds": 2,
    "MesBatchSize": 100,
    "MesMaximumAttempts": 10,
    "MesInitialRetrySeconds": 5,
    "MesMaximumRetrySeconds": 300,
    "MesApiKeyHeader": "",
    "MesApiKey": ""
  }
}
```

生产密钥通过环境变量、安全配置中心或部署平台注入：

```text
AssetHealthGovernance__MesApiKey=<secret>
```

禁止把真实密钥提交仓库。

## 14. 启用顺序

```text
保持 v3.5 Enabled=false 升级
→ 验证 v3.4 Health Scoring 与 SQL History
→ 测试环境开启 v3.5，MES Push 仍关闭
→ 验证 Raised / GradeChanged / Recovery
→ 验证确认和抑制审计
→ 配置 MES 测试地址
→ 开启 MesPushEnabled
→ 验证幂等、超时、5xx、断线、恢复和 DeadLetter
→ 完整 Soak
→ 审批配置和密钥
→ 小范围上线
```

## 15. 回退

关闭全部 v3.5：

```text
AssetHealthGovernance__Enabled=false
```

仅关闭 MES：

```text
AssetHealthGovernance__MesPushEnabled=false
```

关闭 v3.5 不影响 v3.4 健康评分、v3.3 Fusion、规则、ML、周期模型、PLC、任务或调度。

## 16. 监控建议

必须监控：

- ActiveEvents；
- SuppressedActiveEvents；
- Journal IsAvailable；
- PendingDeliveries；
- RetryingDeliveries；
- DeadLetterMessages；
- LastSuccessfulWriteUtc；
- LastSuccessfulDeliveryUtc；
- LastError；
- SQL 表和事务日志增长；
- MES HTTP 时延和错误率。

告警建议：

```text
Journal IsAvailable=false
PendingDeliveries 持续增长
RetryingDeliveries 长时间不下降
DeadLetterMessages > 0
LastSuccessfulDeliveryUtc 长时间不更新且存在 Pending
活动事件快速增长但健康评分资产数稳定
```

## 17. 已知边界

- Event Journal 依赖 SQL Server；
- MES 推送依赖 HTTP 网络；
- v3.5 没有本地 SQL Journal WAL；
- SQL 写入发生在诊断后台服务，数据库不可用时该次转换会失败并在后续评估重试相关状态；
- 写接口的最终授权策略需接入项目身份体系；
- v3.5 不提供根因、维修建议、ONNX 或 RUL。

这些边界不会影响 PLC 安全联锁和实时调度。
