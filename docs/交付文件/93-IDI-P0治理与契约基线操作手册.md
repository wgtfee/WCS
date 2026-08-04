# IDI-P0 治理与契约基线操作手册

## 1. 适用范围

本手册用于开发、测试和运维 IDI-P0 治理基线。P0 只提供只读检查和治理契约，不执行模型切换、特征物化、调度写入或 PLC 控制。

## 2. 环境

推荐使用：

```text
ASPNETCORE_ENVIRONMENT=IndustrialIntelligence
```

对应配置：`src/Wcs.Host/appsettings.IndustrialIntelligence.json`。

该环境固定 `Simulator.Enabled=true`，避免专用治理 Host 启动真实 PLC 轮询。

Production 中即使误配 `IndustrialIntelligence.Enabled=true`，Environment Guard 仍必须拒绝。

## 3. 只读 API

```text
GET /api/industrial-intelligence/status
GET /api/industrial-intelligence/capabilities
```

正常 P0 Status 应包含：

```text
Stage=IDI-P0
ReadOnly=true
ControlWriteAllowed=false
ProductionAllowed=false
MaximumAutomationLevel=L0 或 L1
```

未批准环境和 Production 返回 404。

## 4. 配置检查

启动前检查：

```text
IndustrialIntelligence.Enabled
IndustrialIntelligence.Mode
IndustrialIntelligence.AllowedEnvironments
IndustrialIntelligence.MaximumAutomationLevel
IndustrialIntelligence.MaximumPendingProposals
IndustrialIntelligence.EvidenceRetentionDays
IndustrialIntelligence.MaximumModelPackageBytes
IndustrialIntelligence.MaximumDatasetRows
```

P0 不允许 `MaximumAutomationLevel > L1`。

任何边界值非法时应修正配置，不得通过代码删除校验。

## 5. Evidence 与 Hash

SHA-256 必须是 64 位十六进制。不要手工伪造 Evidence 摘要；EvidenceId、Subject、Version、Actor、CorrelationId 必须来自实际治理流程。

同一 Version 对应不同 Hash 时，不覆盖旧对象，应创建新版本或拒绝冲突。

## 6. Audit Journal

Audit Journal 只追加。每条记录至少包含：

```text
AuditId
Action
TargetType
TargetId
Actor
Reason
OccurredAtUtc
CorrelationId
```

重复 AuditId 必须失败。P0 不提供修改或删除历史审计记录的服务接口。

## 7. SQL Schema

P0 定义：

```text
Wcs_IndustrialIntelligenceAuditJournal
Wcs_IndustrialIntelligenceEvidence
```

建立索引时应确认没有修改已有 WCS 控制业务表。现场大表操作应在维护窗口执行。

## 8. 专项测试

本地/CI 等价命令：

```bash
dotnet restore src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj
dotnet build src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj -c Release --no-restore
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj \
  -c Release --no-build \
  --filter 'FullyQualifiedName~IndustrialIntelligenceGovernanceTests'
```

必须精确 14/14。

## 9. 静态安全检查

如果专项 workflow 报 forbidden control dependency：

1. 找到 P0 项目中新增的 PLC/CommandBus/Task/Dispatch/Traffic 类型；
2. 删除直接依赖；
3. 如果确实需要业务信息，只引入只读 DTO/Snapshot 契约；
4. 不允许通过缩小 grep 范围、删关键词或白名单绕过。

## 10. 47-child 累计回归

P0 Functional Head 稳定后运行：

```text
WCS IDI P0 Full Regression
```

要求 47/47 exact-head。

如果某个历史负载门禁出现 Runner/RSS 暂态：

- 保持同一 Head；
- 保持原阈值；
- 允许重跑失败 Job；
- 不允许降低内存、吞吐、Soak 或测试数量门槛。

如果修改任何代码或文档导致 Head 改变，则必须重新生成 P0 专项和 47-child 最终证据。

## 11. Artifact 核验

最终至少核对：

- Run conclusion=success；
- Artifact ID；
- Artifact Name；
- Expired=false；
- SHA-256 Digest；
- Artifact 对应当前 PR Head；
- Full Regression evidence.json 中 workflowCount=47、allSuccess=true。

## 12. 发布

P0 完成后仅允许发布为：

```text
Industrial Intelligence Governance Baseline
Automation Level <= L1
ReadOnly / Advisory only
```

不得描述为 AI 自动控制投产。

## 13. 回滚

P0 回滚优先级：

1. `IndustrialIntelligence.Enabled=false`；
2. MaximumAutomationLevel 降为 L0；
3. 回滚应用版本；
4. 保留 Audit/Evidence 历史，不删除审计数据。

P0 故障不得影响 WCS 原确定性控制链路。
