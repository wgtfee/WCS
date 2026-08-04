# IDI-P0 治理与契约基线设计说明

## 1. 阶段定位

IDI-P0 是 `WCS Industrial Decision Intelligence v4.0` 的第一阶段。P0 不实现模型发布、特征物化、智能决策或自动控制，只固定后续 P1～P6 必须遵守的治理、安全、版本、Evidence、审计与环境边界。

分支：`feature/idi-p0-governance-contracts`。

## 2. 目标

- 建立独立 `Wcs.IndustrialIntelligence` 项目；
- 固定 L0～L4 `AutomationLevel`；
- 建立 `IndustrialIntelligenceOptions` 和 fail-closed Environment Guard；
- 建立 `EvidenceReference`、`VersionedHashReference`、`ActorReason`、`BoundedQuery`；
- 固定 Model/Feature/Proposal/Optimization 生命周期状态；
- 建立 append-only Audit Journal 契约；
- 定义 P0 两张 SQL 治理表；
- Host 只暴露只读 Status/Capabilities；
- Production 永久 fail-closed；
- P0 软件最大自动化等级限制为 L1；
- 禁止 P0 引用 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic 或 Route Reservation 写路径。

## 3. 核心目录

```text
src/Wcs.IndustrialIntelligence/
  Governance/IndustrialIntelligenceContracts.cs

src/Wcs.Infrastructure/IndustrialIntelligence/
  IndustrialIntelligenceGovernanceEntities.cs

src/Wcs.Host/Controllers/
  IndustrialIntelligenceController.cs

src/Wcs.Simulator.Tests/
  IndustrialIntelligenceGovernanceTests.cs
```

## 4. 自动化等级

| 等级 | 定义 | P0 |
|---|---|---|
| L0 | 只读解释/状态 | 允许 |
| L1 | 建议，不执行控制 | 允许上限 |
| L2 | 人工批准后执行有限动作 | 禁止 |
| L3 | 规则约束自动执行 | 禁止 |
| L4 | 安全相关自主控制 | 永久不由通用 AI 直接执行 |

`IndustrialIntelligenceEnvironmentGuard` 在 P0 对 `MaximumAutomationLevel > L1` 直接拒绝。

## 5. 环境边界

`Production` 无条件拒绝，即使配置错误地把 Production 放进 `AllowedEnvironments` 也不能开启。

允许的专用环境由配置显式给出，例如：

```text
Development
IndustrialIntelligence
IndustrialIntelligenceLoadTest
```

`appsettings.IndustrialIntelligence.json` 使用 `Simulator.Enabled=true`，防止专用治理 Host 启动真实 PLC 轮询。

## 6. Host API

```text
GET /api/industrial-intelligence/status
GET /api/industrial-intelligence/capabilities
```

禁止在 P0 Controller 中出现 POST/PUT/PATCH/DELETE。

Status 固定表达：

```text
Stage=IDI-P0
ReadOnly=true
ControlWriteAllowed=false
ProductionAllowed=false
EvidenceRequired=true
AuditRequired=true
```

Capabilities 仅将 GovernanceContracts、EvidenceGovernance、AuditJournal 标记为 P0 可用；ModelOps、Feature Center、Shadow Decision、Maintenance Learning、Optimizer、Bounded Automation 均显示为后续阶段。

## 7. 版本与 Evidence

所有可治理对象后续都必须能追踪：

```text
Version
SHA-256
Actor
Reason
CreatedAtUtc
CorrelationId
```

同一 Version 对应不同 Hash 必须视为新版本冲突，不能静默覆盖。

`EvidenceReference` 的 SHA-256 必须为 64 位十六进制；EvidenceId 必须唯一，Evidence 历史不得覆盖。

## 8. 审计

P0 定义 append-only Audit Journal。审计最小字段：

```text
AuditId
Action
TargetType
TargetId
Actor
Reason
OccurredAtUtc
CorrelationId
PayloadHash
```

相同 AuditId 再次 Append 必须失败，不提供 Update/Delete 契约。

## 9. SQL 治理表

```text
Wcs_IndustrialIntelligenceAuditJournal
Wcs_IndustrialIntelligenceEvidence
```

P0 仅新增治理表和索引，不修改现有控制域表。Schema 定义独立放在 Infrastructure/IndustrialIntelligence 下。

## 10. 有界配置

P0 对 Proposal 数量、保留期、推理超时、模型包大小、模型加载数、并发推理、Snapshot 保留期和 Dataset 行数设上限。非法值不是自动放宽，而是 fail-closed。

## 11. 安全依赖禁止清单

P0 Core 静态扫描至少禁止：

```text
IPlcConnection
IPlcClient
S7Client
Snap7
S7CommPlusDriver
PlcWriter
CommandBus
TaskScheduler
TaskOrchestrator
DeviceManager
UnifiedTransportDispatchEngine
TransportTrafficCoordinator
IRouteReservationManager
```

## 12. CI 与 Evidence

专项 workflow：

```text
WCS IDI P0 Governance Contract
```

固定 14/14。

累计 workflow：

```text
WCS IDI P0 Full Regression
```

继承 S10 46-child + P0 专项，共固定 47-child exact-head。

最终 Evidence 必须包含 Head、workflowCount=47、allSuccess=true、ControlWriteAllowed=false、MaximumAutomationLevel=L1、Artifact 和 Digest。

## 13. 完成定义

P0 软件完成要求：

- 14/14 专项成功；
- 47/47 exact-head 成功；
- PR Head 未移动；
- Production fail-closed；
- P0 控制写依赖为 0；
- API 仅两个 GET；
- Audit/Evidence/Hash/ActorReason 契约通过；
- SQL Schema 与索引定义完成；
- Artifact/Digest 核实；
- 91～93 文档和总索引同步；
- Squash 合入 develop。

P0 完成只代表工业决策智能治理软件基线完成，不代表任何自动控制或现场安全验收完成。
