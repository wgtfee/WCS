# IDI-P2 Feature Center 设计说明

## 目标
统一 FeatureDefinition、FeatureSchema、FeatureSnapshot、实时/历史物化、质量、血缘与 Point-in-Time Dataset，同时保持确定性 WCS 控制链路完全独立。

## 已实现软件范围
- 独立 `Wcs.FeatureCenter` 项目与不可变 Definition/Schema/Snapshot/Dataset/Quality/Lineage 契约。
- DefinitionHash、SchemaHash、ValuesHash 与 Snapshot 身份确定性生成。
- Freshness、NullPolicy(Fail/Default/Ignore)、ValidRange 与 QualityEvent。
- SourceOffset、实时/历史物化、受界保留与重启恢复。
- SQL-backed Definition/Schema/SchemaItem/Snapshot/Quality/Dataset/Lineage 持久化。
- Point-in-Time Dataset 按 AsOfUtc 截断，禁止未来/Outcome 后数据泄漏。
- v3.9 固定 14 维 Forecast FeatureSchema 治理映射与兼容验证。
- FeatureSchema 与 ModelManifest 精确匹配。
- bounded Host 查询/管理接口。

## 安全边界
Feature Center 不依赖 PLC、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch、Traffic 或 RouteReservation mutation。SQL/历史存储/Feature Center 故障不得阻塞控制线程。P2 保持 `MaximumAutomationLevel<=L1`、`ControlWriteAllowed=false`、`ProductionAutomationAllowed=false`。

## 数据表
`Wcs_FeatureDefinition`、`Wcs_FeatureSchema`、`Wcs_FeatureSchemaItem`、`Wcs_FeatureSnapshot`、`Wcs_FeatureQualityEvent`、`Wcs_FeatureDataset`、`Wcs_FeatureLineage`。
