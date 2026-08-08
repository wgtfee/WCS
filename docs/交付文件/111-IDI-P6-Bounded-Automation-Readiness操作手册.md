# IDI-P6 Bounded Automation Readiness 操作手册

## 1. 使用范围

本手册用于查看 IDI-P6 软件侧治理状态和 Evidence。P6 不提供生产自动化启用操作，最终声明固定为：

> `software-side ready only`

任何运维人员都不能把 P6 页面或 API 当成 PLC 控制、调度执行、安全复位、路权释放或 Production L2/L3 授权入口。

## 2. 启用前提

P6 的 Host 只读接口沿用 `IndustrialIntelligence` 配置和 P0 Environment Guard。推荐非 Production 环境保持：

```json
{
  "IndustrialIntelligence": {
    "Enabled": true,
    "Mode": "ReadOnly",
    "AllowedEnvironments": ["IndustrialIntelligence"],
    "MaximumAutomationLevel": "L1"
  }
}
```

P0 仍会拒绝 Production 和 L1 以上 Host 运行权限。不要为了查看 P6 页面修改此安全边界。

## 3. 查看系统状态

调用：

```text
GET /api/bounded-automation-readiness/status
```

重点确认：

- `finalClaim = software-side ready only`；
- `defaultsDisabled = true`；
- `productionEnablementAllowed = false`；
- `controlWriteAllowed = false`；
- `executionApiExposed = false`；
- `approvalApiExposed = false`；
- `rollbackExecutionApiExposed = false`；
- `permanentProhibitionCount = 11`。

如果 Production 环境返回 404，这是预期的 fail-closed 行为，不应通过修改 Controller 绕过。

## 4. 查看永久禁止项

调用：

```text
GET /api/bounded-automation-readiness/prohibitions
```

应显示 11 项：EmergencyStop、SafetyReset、SafetyDoorBypass、LightCurtainBypass、MechanicalInterlockBypass、PlcForceWrite、AutomaticRoadRightRelease、AutomaticBlockRelease、UnapprovedShutdown、StateMachineBypass、TrafficConstraintBypass。

这些项目不存在“临时允许”操作。

## 5. 查看 Evidence

最近 Evidence：

```text
GET /api/bounded-automation-readiness/evidence?limit=100
```

单条 Evidence：

```text
GET /api/bounded-automation-readiness/evidence/{evaluationId}
```

`limit` 范围为 1～500。Evidence 主要字段：

- EvaluationId；
- RequestedLevel；
- PolicyVersion / PolicyHash；
- SoftwareHeadSha；
- SourceEvidenceHash；
- DecisionHash；
- SoftwareSideReady；
- ProductionEnablementAllowed；
- Claim；
- Reasons。

发现 `ProductionEnablementAllowed=true` 或 Claim 不是 `software-side ready only` 时，应视为 Evidence 异常并停止验收。

## 6. Desktop 页面

菜单进入：

```text
IDI-P6 Bounded Automation Readiness
```

页面只允许：

- 刷新只读状态；
- 查看 permanent prohibitions；
- 查看最近 Evidence；
- 输入 EvaluationId 查询单条 Evidence。

页面不应出现：

- Enable / Start Automation；
- Approve Production；
- Execute；
- KillSwitch 操作；
- Rollback Execute；
- PLC Write；
- Release Road Right / Block；
- Safety Reset。

若未来出现上述按钮，应先视为 P6 边界回归，而不是新功能。

## 7. L2/L3 Readiness 解读

P6 的 L2/L3 是“软件侧 readiness 评估等级”，不是实际生产授权。

没有以下四类真实 Evidence 时必须拒绝：

1. Site Evidence；
2. HIL Evidence；
3. Independent Safety Approval Evidence；
4. Rollback Evidence。

即使四类都具备，P6 最多显示：

```text
SoftwareSideReady = true
ProductionEnablementAllowed = false
Claim = software-side ready only
```

## 8. SQL Evidence 运维

表：

```text
Wcs_BoundedAutomationReadinessEvidence
```

该表按 append-only Evidence 使用：

- 不手工 UPDATE DecisionHash；
- 不手工把 ProductionEnablementAllowed 改为 true；
- 不复用 EvaluationId 写入不同 DecisionHash；
- 不把该表当成执行队列；
- 出现不一致记录时保留原始数据并按审计流程定位来源。

相同 EvaluationId + 相同 DecisionHash 重放属于幂等行为；相同 ID + 不同 hash 必须拒绝。

## 9. 开发人员验证命令

Governance + Evidence/API：

```bash
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj -c Release --filter 'FullyQualifiedName~BoundedAutomationReadinessTests'
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj -c Release --filter 'FullyQualifiedName~BoundedAutomationEvidenceContractTests'
```

Stress：

```bash
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj -c Release --filter 'FullyQualifiedName~BoundedAutomationReadinessStressTests'
```

SQL 集成测试必须设置 `WCS_P6_SQL_CONNECTION` 并连接测试 SQL Server：

```bash
dotnet test src/Wcs.Simulator.Tests/Wcs.Simulator.Tests.csproj -c Release --filter 'FullyQualifiedName~BoundedAutomationSqlEvidenceTests'
```

Desktop 编译：

```bash
dotnet build src/Wcs.Desktop/Wcs.Desktop.csproj -c Release
```

## 10. GitHub Acceptance Gates

最终必须同时通过：

- `WCS IDI P6 Bounded Automation Readiness Contract` — 54 Specialty；
- `WCS IDI P6 Automation Readiness Stress Soak` — 6×3 Stress + 54 Specialty recheck；
- `WCS IDI P6 Readiness SQL Evidence` — 6 SQL tests；
- `WCS IDI P6 Full Regression` — exactly 56 child；
- `WCS One Hour Soak Load` — exact Acceptance Head success。

最终还要核对 Artifact digest 与实际下载 ZIP SHA-256 一致。

## 11. 故障处理

### 11.1 GitHub Hosted Runner 5xx

如果 Setup/Action download 出现 GitHub 5xx/Bad Gateway/Service Unavailable，先检查是否为 runner 基础设施问题。仅在确认没有业务步骤失败后，可对同一 exact-head failed jobs 重跑；不得删除 child 或降低门禁。

### 11.2 Specialty 编译失败

优先检查：

- Core governance contract；
- Host GET-only Controller；
- Desktop XAML/DI；
- Evidence record/hash；
- Infrastructure project reference。

不得通过跳过 Desktop build 或减少 54 个测试来收口。

### 11.3 SQL Gate 失败

检查：

- SQL Server service container health；
- connection string；
- CodeFirst schema；
- unique EvaluationId index；
- append-only conflict detection。

不得改成内存数据库来代替正式 SQL gate。

## 12. 发布边界

P6 合并到 develop 后，仍不得把它描述为 Production Automation 功能。发布说明只能声明软件侧治理、Evidence、只读观测和 readiness 评估能力已经具备；真实现场授权仍属于未来独立安全流程。
