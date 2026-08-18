# IDI-P2 Feature Center 专项测试报告

## 固定专项门禁
`WCS IDI P2 Feature Center Contract` 冻结为 exactly 30 tests。覆盖 DefinitionHash/SchemaHash/ValuesHash、Freshness、NullPolicy、ValidRange/QualityEvent、Snapshot、SourceOffset、Point-in-Time 防泄漏、v3.9 固定14维兼容、FeatureSchema↔ModelManifest、受界限制和 zero-control 静态边界。

## 累计回归
`WCS IDI P2 Full Regression` 固定为 exactly 49 software children：P1 已完成的 48 个正式软件 child + P2 Specialty 1 个。矩阵要求每个 child `completed/success`、每个 `headSha` 等于同一 exact Head、`workflowCount=49`、`allSuccess=true`，并包含 P0 Governance、P1 ModelOps、P2 Feature Center 与 One Hour Soak。

## 软件完成边界
本报告仅表示仓库软件门禁；不替代真实 HIL、Protocol、Mechanical Safety 或 Site Acceptance。Feature Center 仍为 L0/L1 数据治理能力，不产生控制写入。
