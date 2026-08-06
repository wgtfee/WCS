# IDI-P3 Shadow Decision 专项测试报告

## 1. 验收原则

P3 测试不得以删除测试、降低阈值、复用旧 Head 成功 Run、减少累计 child 或排除 One Hour Soak 的方式获得通过。任何最终 Head 变化后，Specialty 与累计 Full Regression 必须重新在新 exact Head 执行。

## 2. 专项范围

专项契约覆盖：

- 无 FeatureSnapshot 不生成 Proposal；
- 无 Champion 返回 ModelUnavailable；
- Hard Constraint 阻断高分候选并保存完整原因；
- Explanation 绑定模型、特征、规则 Evidence；
- Proposal 幂等和同输入/版本重放一致；
- 过期 Proposal 不可批准；
- 不同 Actor approve/reject 审计；
- Shadow Proposal 不修改任务、车辆、路线或 PLC；
- Outcome 可关联实际任务/维修结果；
- Proposal 队列和保留期有界；
- P3 domain 无 PLC/CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic/RouteReservation/SQL/HTTP 依赖。

## 3. 当前功能 Head 证据

功能 Head `a8a572d85651ce75ff79eb1297ff32dae7f9de74`：

- `WCS IDI P3 Shadow Decision Contract` Run `31053479326`：24/24 success；
- Specialty Artifact ID `8949367681`；
- Artifact `wcs-idi-p3-shadow-decision-11`；
- expired=false；
- SHA-256 `9313dda3f3ba843e165c7e416488dc515c93e7439d26386902578a8547c6d962`；
- Artifact head_sha 与功能 Head 一致。

同一功能 Head：

- `WCS IDI P3 Full Regression` Run `31053479262`：exactly 50 child completed/success；
- `workflowCount=50` / `allSuccess=true` 门禁成功；
- PR Head 未漂移检查成功；
- Full Regression Artifact ID `8950683850`；
- Artifact `wcs-idi-p3-full-regression-3`；
- expired=false；
- SHA-256 `b949aa9e725f287707f9364ed951f706ca86c96151115c0a8180ac5c48459ea9`；
- Artifact head_sha 与功能 Head 一致。

One Hour Soak、Windows CI、End-to-End Load、P0 Governance、P1 ModelOps、P2 Feature Center 在该功能 Head 上均成功。

## 4. 当前状态

上述证据是功能 Head 证据，不是最终 Evidence Head。由于交付文档及剩余 Host/Desktop/持久化收口会移动 PR Head，最终 Ready 前必须在最终 exact Head 重新执行 Specialty 与 50-child Full Regression，并重新核实 Artifact/Digest/head_sha。