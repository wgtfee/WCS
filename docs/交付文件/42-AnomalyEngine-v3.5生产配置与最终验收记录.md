# AnomalyEngine v3.5 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能版本 | v3.5 健康事件治理与 MES HTTP Outbox |
| PR | `#28` |
| 研发分支 | `feature/anomaly-health-governance-v3-5` |
| 目标分支 | `develop` |
| 软件验收基线 | `1315ba7108da545222252747fff00072f19a0219` |
| 软件验收状态 | 通过 |
| 现场投产状态 | 未声明，仍需项目级联调与签署 |
| 安全边界 | 诊断与通知，不进入 PLC、设备停止、任务、路线、路权或调度控制 |

## 2. 生产安全默认值

仓库中的基础与生产配置均保持：

```json
{
  "AssetHealthGovernance": {
    "Enabled": false,
    "MesPushEnabled": false,
    "MesBaseUrl": "",
    "MesApiKeyHeader": "",
    "MesApiKey": ""
  }
}
```

含义：

- 部署新版本不会自动创建健康事件；
- 不会自动向 MES 发送 HTTP；
- Git 中不保存生产 MES 地址和密钥；
- 未完成现场阈值、身份、网络、MES 契约和投产审批前不得开启。

## 3. 数据库对象

表：

```text
Wcs_AssetHealthEventJournal
```

关键约束：

```text
UX_Wcs_AssetHealthEventJournal_MessageId
UX_Wcs_AssetHealthEventJournal_EventVersion
IX_Wcs_AssetHealthEventJournal_Event
IX_Wcs_AssetHealthEventJournal_Delivery
IX_Wcs_AssetHealthEventJournal_Occurred
```

上线前必须确认表、索引、磁盘、事务日志、备份、保留期和应用账号最小权限。

## 4. 必须外部注入的参数

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__WcsDb=<生产 SQL Server 最小权限连接字符串>
```

启用治理前必须审批：

```text
AssetHealthGovernance__Enabled=true
AssetHealthGovernance__MinimumEventGrade=Degraded
AssetHealthGovernance__ConsecutiveUnhealthyEvaluations=...
AssetHealthGovernance__ConsecutiveRecoveryEvaluations=...
AssetHealthGovernance__EvaluationIntervalSeconds=...
```

启用 MES 前还必须外部注入并验收：

```text
AssetHealthGovernance__MesPushEnabled=true
AssetHealthGovernance__MesBaseUrl=https://...
AssetHealthGovernance__MesEndpointPath=/...
AssetHealthGovernance__MesApiKeyHeader=...
AssetHealthGovernance__MesApiKey=...
```

## 5. 启用顺序

```text
Fusion 只读观察通过
→ v3.4 健康评分和 SQL 历史通过
→ v3.5 Enabled=true、MesPushEnabled=false
→ 事件阈值、确认、抑制和恢复人工验收
→ MES 沙箱契约与幂等联调
→ 网络中断、5xx、DeadLetter 和恢复演练
→ 权限与审计验收
→ MesPushEnabled=true
```

任何步骤失败时停止进入下一步骤。

## 6. 最终自动化验收

代码基线共 13 项工作流全部成功：

- WCS Asset Health Governance #9；
- WCS Asset Health Governance Compile #4；
- WCS Anomaly Health Scoring #34；
- WCS Anomaly Health Scoring SQL #16；
- WCS Windows CI #228；
- WCS End-to-End Load #157；
- WCS PLC Telemetry Storage Load #52；
- WCS PLC Anomaly Engine Load #169；
- WCS PLC Anomaly Engine Soak #152；
- WCS Anomaly Fusion Load #60；
- WCS Anomaly Fusion Bridge E2E #52；
- WCS Transport Cycle Analysis #55；
- WCS One Hour Soak Load #123。

专项证据：

```text
Run ID: 30320836736
Artifact: wcs-asset-health-governance-9
Digest: sha256:3b558cfe7875004cf85c61a9dd0f35774d4df915f6c0efe3a600d2a49a852a4c
```

详细测试口径见文档 41。

## 7. 上线监控

持续监控：

- Active、Acknowledged、Suppressed 事件数；
- Journal IsAvailable 与 LastError；
- Pending、Retrying、Delivered、DeadLetter；
- LastSuccessfulWriteUtc 与 LastSuccessfulDeliveryUtc；
- MES HTTP 状态码、时延和网络错误；
- SQL 表、数据文件和事务日志增长；
- Host CPU、托管内存和 RSS；
- 治理 API 的操作者与审计记录。

## 8. 回退

第一层：停止外部通知。

```text
AssetHealthGovernance__MesPushEnabled=false
```

第二层：停止事件治理。

```text
AssetHealthGovernance__Enabled=false
```

回退后不得删除 Journal；应保存未发送、DeadLetter、确认和抑制记录用于审计。关闭 v3.5 不影响 PLC、任务、调度、Fusion 和 v3.4 历史。

## 9. 现场验收清单

- [ ] 生产 SQL 账号、表、索引、备份和保留期已审批；
- [ ] 资产编号与 MES 主数据一致；
- [ ] MinimumEventGrade 和连续次数已现场验证；
- [ ] MES JSON 契约、Idempotency-Key 和 409 语义已确认；
- [ ] HTTPS、认证头、密钥和网络白名单已配置；
- [ ] MES 5xx、超时、断线、恢复和 DeadLetter 演练通过；
- [ ] acknowledge、suppress、unsuppress 和 retry 权限已接入身份系统；
- [ ] Actor、Reason、Note 可审计；
- [ ] 只读观察期无不可接受误报、漏报或消息风暴；
- [ ] 明确 v3.5 不替代 PLC 联锁、AlarmCenter 或人工处置；
- [ ] 设备、工艺、生产、MES、运维和信息化负责人签署。

## 10. 最终结论

- [x] 健康事件生命周期完成；
- [x] SQL Journal 与幂等完成；
- [x] MES Outbox、退避、DeadLetter 与重放完成；
- [x] Host 重启恢复完成；
- [x] 完整 CI 与一小时 Soak 成功；
- [x] 生产默认关闭且无仓库明文凭据；
- [x] 无 PLC 写入、停机、任务取消或调度修改。

**v3.5 仓库级软件研发和自动化验收完成。现场投产状态仍需项目级签署。**
