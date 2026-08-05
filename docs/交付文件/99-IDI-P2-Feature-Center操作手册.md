# IDI-P2 Feature Center 操作手册

## 使用原则
1. Production 默认 fail-closed；未批准环境不得启用 Industrial Intelligence。
2. Feature Definition/Schema 变更必须形成新版本/Hash，不覆盖历史版本。
3. Snapshot 在推理或 Proposal 前冻结；重放必须使用原 SchemaHash、ValuesHash、AsOfUtc 与 SourceOffset。
4. Dataset 必须使用 Point-in-Time 构建；任何晚于 AsOfUtc 或 Outcome 后数据不得进入训练输入。
5. 高频历史数据使用受治理外部文件/时序存储元数据，SQL 不建立无限增长逐点 EAV。
6. SQL/历史存储异常时 Feature Center fail closed/fail isolated，不等待、不阻塞 WCS 控制链路。

## 故障恢复
重启后从 SQL 恢复 Definition、Schema、Snapshot/Dataset 元数据与 Lineage；实时缓存由 SourceOffset 和既有事件/历史物化重建。无法恢复完整证据时标记不可用或 Stale，不伪造新鲜值。

## 安全声明
P2 不允许 PLC、任务、设备、车辆、路线、路权或交通控制 mutation；`ControlWriteAllowed=false`、`ProductionAutomationAllowed=false`。
