# S9 真实 HIL 与试运行软件专项测试报告

## 1. 报告定位

本报告记录 S9 在进入真实 HIL 前的仓库级软件验证。它证明 HIL 治理框架、证据契约、Abort/Recovery、只读 Host 边界和 exact-head 软件回归满足设计要求，但**不等于真实 HIL、机械安全或现场验收通过**。

S8 Evidence Head：

```text
02b202862816a91ff473925bb964e4d2aa2f6470
```

S9 最终软件 Functional Head、Run、Artifact 与 Digest 必须在 PR #42 软件侧代码冻结后回填；任何 Head 变化都会使旧证据失效。

## 2. S9 Governance Contract

工作流：

```text
WCS S9 HIL Governance Contract
```

固定测试计数：26。

覆盖范围：

- Disabled 默认 fail-closed；
- Production 不允许进入 HIL AllowedEnvironments；
- Hardware Profile 必须隔离生产网络、禁用生产凭据并具备批准信息；
- Trial Plan Asset 必须属于批准 Hardware Profile；
- S8EvidenceHead / SoftwareHead 必须是 exact Git SHA；
- Operator / SafetyApprover 双人审批；
- hosted runner 不能 BeginExecution；
- SelfHostedHil 必须带 `self-hosted` 与 `wcs-hil` 标签；
- Bench、Software Head、网络隔离、凭据必须匹配；
- 每个 Step 都要求真实硬件证据与 SHA-256；
- Evidence Sequence 严格递增且 Session Duration 有界；
- Failed Step 不能被后续 Passed 覆盖成成功；
- Abort 立即终止 Session；
- Recovery 必须验证物理安全状态；
- Recovered Session 不能重新 Running；
- Replacement Session 必须显式引用 RecoveryFromSessionId；
- Acceptance 需要协议、机械安全、现场验收及独立证据 SHA-256；
- EvidenceHash 对等价输入确定性一致。

已观察到的软件验证记录中，扩展后的 Governance Contract 已有 26/26 success；最终交付仍以冻结 Head 上的 Run 为准。

## 3. Software Trial Readiness

工作流：

```text
WCS S9 Software Trial Readiness
```

固定测试计数：8。

覆盖范围：

- `HilVerificationOptions.Enabled` 默认 false；
- Production 无条件 404；
- 只允许 HIL / TrialRun；
- 配置错误 fail-closed；
- HIL Controller 仅两个 GET；
- 不存在 POST/PUT/PATCH/DELETE；
- HIL / TrialRun inspection Host 使用 `Simulator.Enabled=true` 阻止真实 PLC background service；
- Real HIL workflow 只能 manual + `[self-hosted, wcs-hil]`；
- hosted CI 明确记录 `real_hil=false`、`site_acceptance=false`。

早期一次 Readiness Run 暴露了 IConfiguration 数组绑定会将默认 `AllowedEnvironments` 与配置数组拼接、造成重复项而被 fail-closed 的真实缺陷。修复方式是 Controller 绑定前将 Allow-list 初始化为空数组，再显式 Bind；没有降低任何安全门槛。

## 4. 45-child 软件全回归

工作流：

```text
WCS S9 Software Full Regression
```

继承 S8 43 条 exact-head child，并追加：

```text
44 WCS S9 HIL Governance Contract
45 WCS S9 Software Trial Readiness
```

验收要求：

```text
workflowCount=45
allSuccess=true
45/45 status=completed
45/45 conclusion=success
45/45 headSha=<exact S9 software acceptance Head>
One Hour Soak=success
PR Head expected==actual
realHilGateIncluded=false
realHilEvidenceRequiredForS9Completion=true
```

45/45 只代表 S9 软件侧已具备进入真实台架的条件；真实 HIL Gate 故意不包含在 hosted/software Matrix 中。

## 5. Real HIL Evidence Gate

工作流：

```text
WCS S9 Real HIL Evidence Gate
```

该 Gate 不是自动 PR CI：

- 仅 `workflow_dispatch`；
- `runs-on: [self-hosted, wcs-hil]`；
- 使用 `wcs-hil` protected Environment；
- 输入 exact Software Head / SessionId / BenchId；
- 从现场 Runner 本地读取 Evidence Manifest / Bundle；
- 重新计算 SHA-256；
- 验证 `RealHardwareConnected=true`；
- 验证所有 Step `Passed + RealHardwareObserved=true`；
- 验证 Protocol / Mechanical Safety / Site Acceptance；
- 成功后输出 90 天 Artifact。

没有真实 Runner 和真实证据时不得手工构造一个“成功”的 Gate。

## 6. 缺陷处理原则

- 测试失败先判断真实逻辑缺陷、配置缺陷和 Runner 暂态；
- 真实逻辑缺陷必须修代码并形成新 Head；
- Runner RSS/Working Set 暂态只允许在同一 exact Head、原阈值下重跑；
- 禁止降低安全阈值、删除测试、`continue-on-error` 或用旧 Head 替代；
- Real HIL 未执行时，软件报告必须明确写 `RealHilExecuted=false / Pending`。

## 7. 最终交付证据表

软件代码冻结后回填：

| Gate | Run | Tests/Matrix | Artifact | Digest | Exact Head |
|---|---|---|---|---|---|
| S9 HIL Governance Contract | 待冻结 | 26/26 | 待冻结 | 待冻结 | 待冻结 |
| S9 Software Trial Readiness | 待冻结 | 8/8 | 待冻结 | 待冻结 | 待冻结 |
| S9 Software Full Regression | 待冻结 | 45/45 | 待冻结 | 待冻结 | 待冻结 |
| S9 Real HIL Evidence Gate | **待现场** | Real HIL | **待现场** | **待现场** | 必须等于批准 Software Head |

在 Real HIL 行仍为“待现场”时，PR #42 必须保持 Draft，S9 不得声明完成。
