# IDI-P0 治理与契约基线专项测试报告

## 1. 测试范围

本报告用于记录 IDI-P0 治理与契约基线的软件专项验证。当前阶段不测试 ModelOps、Feature Center、Decision、Optimization 或自动控制功能。

专项 workflow：`WCS IDI P0 Governance Contract`。

## 2. 固定专项数量

`IndustrialIntelligenceGovernanceTests` 固定为 14 条，覆盖：

1. Production 无条件 fail-closed；
2. Enabled=false 拒绝；
3. 未批准环境拒绝；
4. P0 拒绝 L2 及以上；
5. 批准环境允许 L1 ReadOnly；
6. 非法有界参数拒绝；
7. SHA-256 确定性；
8. VersionedHash 拒绝非法 Hash；
9. Evidence 拒绝非法 SHA；
10. Actor/Reason 必填；
11. BoundedQuery 拒绝无界请求；
12. Audit Journal append-only 与重复 AuditId 拒绝；
13. Status API 只读且 Production 404；
14. Controller 只有两个 HttpGet Action。

不得通过减少测试数量来使门禁通过。

## 3. 静态安全验证

专项 workflow 同时扫描：

- `src/Wcs.IndustrialIntelligence` 不得依赖 PLC/CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic/RouteReservation 写路径；
- `IndustrialIntelligenceController` 不得包含 HttpPost/HttpPut/HttpPatch/HttpDelete；
- 必须恰好存在两个 HttpGet；
- `ControlWriteAllowed=false`；
- Production fail-closed 文案和逻辑必须存在；
- P0 两张 SQL 治理表定义必须存在；
- Infrastructure P0 目录不得暴露 Update/Delete 治理实体代码。

## 4. 累计回归

`WCS IDI P0 Full Regression` 固定继承 S10 软件矩阵：

```text
S10 baseline 46 child
+ WCS IDI P0 Governance Contract
= 47 child
```

最终要求：

```text
workflowCount=47
allSuccess=true
every child status=completed
every child conclusion=success
every child headSha=expectedHeadSha
ControlWriteAllowed=false
MaximumAutomationLevel=L1
PR Head unchanged
```

真实 HIL Gate 不纳入该 47-child 软件矩阵。

## 5. 资源与稳定性要求

P0 本身不运行模型推理，不新增高频后台线程，因此专项资源风险主要是：

- Host/Test 编译稳定性；
- 配置绑定无数组重复污染；
- Audit Journal 有界使用；
- SQL Schema 初始化不修改现有控制表；
- 通用历史负载/Soak 门禁在 47-child 中继续保持成功。

## 6. Evidence 记录模板

最终冻结 Head 后填写：

```text
Functional Head:
Evidence Head:

P0 Governance Run:
Test Count: 14/14
Artifact ID:
Artifact Name:
Digest:
Expired: false

P0 Full Regression Run:
Workflow Count: 47/47
Artifact ID:
Artifact Name:
Digest:
allSuccess=true
Head Verification: expected == actual
```

## 7. 当前状态

P0 已进入 PR #44 开发验证阶段。专项首轮运行用于发现编译、契约和静态安全问题；任何修复导致 Head 变化后，旧 Run 只能作为历史诊断证据，不能作为最终验收 Evidence。

## 8. 完成判定

只有 14/14 和 47/47 在同一最终 exact Head 上成功，并核实 Artifact/Digest、Head 未移动后，才允许本报告改为“P0 软件侧完成”。
