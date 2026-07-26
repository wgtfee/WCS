# PLC 异常检测模型交付说明书

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 文档版本 | V1.0 |
| 交付能力 | PLC Telemetry、WAL、AnomalyEngine v1、Isolation Forest v2 |
| 功能实现基线 | `develop@1e7cda6b8d6dfd230293575edc5680bb383971e6` |
| 交付状态 | 软件研发完成，可进入项目集成和现场影子运行 |
| 非交付声明 | 不代表已完成任一具体现场的实车投产验收 |

## 2. 交付范围

本次交付包含：

1. PLC 时序数据可配置存储；
2. SQL Server、InfluxDB 和 Disabled Provider；
3. Buffered 与 WriteAhead 耐久模式；
4. 数据库停机 spool/WAL 和恢复重放；
5. 规则、变化率、持续时间和 Median/MAD 检测；
6. 跨信号一致性检测；
7. 高基数状态治理；
8. Isolation Forest 正常窗口训练；
9. 模型独立校准、版本保存、活动版本和回滚；
10. 在线推理、连续确认和恢复；
11. SQL 异常生命周期；
12. AlarmCenter 桥接；
13. 状态、活动异常和模型管理 API；
14. 单元、精确计数、强制崩溃、准确性、吞吐和持续压测；
15. 架构、功能、使用、测试和交付文档。

## 3. 代码交付清单

### 3.1 Core

```text
src/Wcs.Core/Telemetry/
src/Wcs.Core/AnomalyDetection/
src/Wcs.Core/AnomalyDetection/MachineLearning/
src/Wcs.Core/EventBus/Events/PlcAnomalyEvents.cs
```

关键组件：

- `PlcTelemetryModels`；
- `PlcTelemetryEntity`；
- `PlcAnomalyEngine`；
- `PlcAnomalySampleFactory`；
- `PlcFeatureWindowEngine`；
- `IsolationForest`；
- `PlcMlAnomalyEngine`；
- `PlcAnomalyRecord`；
- 模型、训练和状态接口。

### 3.2 Infrastructure

```text
src/Wcs.Infrastructure/Telemetry/
src/Wcs.Infrastructure/AnomalyDetection/MachineLearning/
```

关键组件：

- SQL Server / InfluxDB Telemetry Store；
- Telemetry Buffer；
- Batch Writer；
- File Spool / WAL；
- File Training Store；
- File Model Store；
- ML Background Service；
- DI 注册和配置绑定。

### 3.3 Host

```text
src/Wcs.Host/BackgroundServices/PlcTelemetryEventBridgeService.cs
src/Wcs.Host/BackgroundServices/PlcAnomalyDetectionService.cs
src/Wcs.Host/BackgroundServices/PlcAnomalyPersistenceService.cs
src/Wcs.Host/BackgroundServices/PlcAnomalyAlarmBridgeService.cs
src/Wcs.Host/Controllers/PlcTelemetryController.cs
src/Wcs.Host/Controllers/PlcAnomalyController.cs
src/Wcs.Host/Controllers/PlcMlAnomalyController.cs
```

LoadTest 和 MlLoadTest 控制器只用于隔离测试环境，不应作为生产业务接口使用。

### 3.4 数据库

- `Wcs_PlcTelemetry`：SQL Server Telemetry Provider 时使用；
- `Wcs_PlcAnomaly`：正式异常生命周期；
- InfluxDB measurement：按现场 `Measurement` 配置；
- 数据库初始化由 Host Infrastructure 初始化流程执行。

## 4. 配置交付清单

### 4.1 必须确认

- `ConnectionStrings:WcsDb`；
- `Storage:Telemetry:Provider`；
- `Storage:Telemetry:DurabilityMode`；
- `Storage:Telemetry:SpoolDirectory`；
- InfluxDB Url、Organization、Bucket/Database 和 Token；
- `AnomalyDetection:Enabled`；
- `AnomalyDetection:Rules`；
- `AnomalyDetection:MachineLearning:Enabled`；
- `ManagementApiEnabled`；
- ModelDirectory；
- TrainingDirectory；
- Profiles、Signals 和阈值。

### 4.2 生产默认建议

```text
Simulator.Enabled=false
ManagementApiEnabled=false
LoadTest/MlLoadTest 环境不可用于生产
MachineLearning 首次上线 RaiseAlarm=false
规则和模型先影子运行
生产密钥不写入 appsettings.json
```

### 4.3 配置文件注意事项

ASP.NET Core 会合并多个配置源，数组可能按下标合并。通用配置中的 `Rules` 和 `Profiles` 应保持空数组，现场完整规则和 Profile 应由最终环境配置提供。

## 5. 数据目录交付清单

| 目录 | 用途 | 是否必须备份 |
|---|---|---:|
| plc-telemetry-spool | spool、WAL 和数据库恢复积压 | 是 |
| anomaly-models | 历史模型和 active.json | 是 |
| anomaly-training | 正常训练窗口特征 | 按项目数据策略 |
| Host logs | 运行、模型加载、持久化和故障日志 | 是 |
| CI evidence | 自动化测试证据 | 是 |

目录不得随程序重新发布而清空。

## 6. 文档交付清单

| 编号 | 文档 | 状态 |
|---|---|---|
| 22 | PLC 异常检测模型架构说明书 | 已交付 |
| 23 | PLC 异常检测功能说明书 | 已交付 |
| 24 | PLC 异常检测模型使用手册 | 已交付 |
| 25 | PLC 异常检测模型测试与验收报告 | 已交付 |
| 26 | PLC 异常检测模型交付说明书 | 本文 |

相关既有文档：

- 06-数据模型与数据库设计说明书；
- 09-部署安装与环境配置手册；
- 11-运维监控与故障处理手册；
- 13-测试计划与验收规范；
- 17-性能容量、仿真与调优指南；
- 18-发布、回退与变更管理手册；
- 19-最终交付清单与签署模板；
- 21-配置参数参考手册。

## 7. 自动化测试证据

必须保留以下工作流成功记录：

- WCS PLC Telemetry Storage Load；
- WCS PLC Telemetry WAL Crash；
- WCS PLC Anomaly Engine Load；
- WCS PLC Anomaly Engine Soak；
- WCS PLC Anomaly ML；
- WCS PLC Anomaly ML E2E；
- WCS PLC Anomaly ML Version Throughput；
- WCS End-to-End Load / Soak；
- Windows Core、Host、Desktop 构建。

软件交付基线已验证：

- SQL/Influx 20 万点精确计数；
- SQL/Influx 5 万点 WAL 强制崩溃恢复；
- v1 213,000 高基数样本和 3,000 精确生命周期；
- v1 10 分钟、631,200 样本和 7,200 生命周期；
- v2 4,000 训练窗口；
- 1,000 未见正常窗口零正式误报；
- 100/100 异常激活；
- 100/100 恢复；
- 10 万窗口和 30 万 RawSignalEvent 全链路；
- 模型双版本、回滚和重启加载。

## 8. 交付部署步骤

### 8.1 部署前

1. 冻结代码和配置版本；
2. 保存 commit SHA；
3. 备份现有数据库和应用配置；
4. 创建数据目录并配置 ACL；
5. 确认磁盘空间和 IO；
6. 确认 SQL/Influx 连接；
7. 导入或确认 PLC 点位；
8. 配置规则但保持报警关闭；
9. MachineLearning 默认关闭或 RaiseAlarm=false；
10. 准备回退包。

### 8.2 启动顺序

```text
SQL Server / InfluxDB
→ 目录和密钥检查
→ Wcs.Host
→ Health 检查
→ Telemetry status
→ Anomaly status
→ ML status
→ Desktop / 上位系统
```

### 8.3 启动后检查

- Host live/ready；
- PLC 连接和轮询；
- telemetry accepted 持续增加；
- dropped=0；
- conservationDelta=0；
- 规则匹配数量符合预期；
- ML Profile 和活动版本符合预期；
- SQL 表可写；
- spool/WAL 无无法解释积压；
- 模型和训练目录可读写；
- 无未处理异常和重复键错误。

## 9. 项目集成阶段

### 9.1 规则集成

每条规则必须关联：

- 设备类型；
- PLC 和点位；
- 工艺含义；
- 正常范围；
- 异常持续时间；
- 严重级别；
- 是否报警；
- 恢复条件；
- 责任人员。

### 9.2 模型集成

每个 Profile 必须关联：

- 设备族和设备清单；
- 产品和负载范围；
- 运行模式；
- 训练数据时间范围；
- 排除的异常时段；
- 训练配置；
- 活动模型版本；
- 误报/漏报验收门槛；
- 回滚版本。

### 9.3 上位系统集成

可通过 SQL、API、AlarmCenter 或已有通知机制集成：

- 活动异常列表；
- 异常恢复；
- 模型版本；
- telemetry 状态；
- 规则和 ML 失败指标；
- 设备、任务和异常关联。

## 10. 现场影子运行

### 10.1 阶段一：只采集

- Telemetry 开启；
- 规则和 ML 关闭；
- 验证数据完整、时间戳、字段名和容量。

### 10.2 阶段二：规则观察

- v1 开启；
- `RaiseAlarm=false`；
- 统计规则命中、正式异常和恢复；
- 修正规则和点位。

### 10.3 阶段三：训练和 ML 观察

- 明确正常时段；
- 收集训练窗口；
- 手动训练和审批；
- `RaiseAlarm=false`；
- 观察至少覆盖主要班次、产品和负载。

### 10.4 阶段四：受控报警

- 只对已验证规则和 Profile 开启报警；
- 从 Warning 开始；
- 监控误报、漏报和恢复；
- 保留一键关闭和模型回滚能力。

## 11. 验收要求

### 11.1 软件交付验收

- [x] 代码进入 `develop`；
- [x] 单元和自动化测试通过；
- [x] 精确计数和故障恢复通过；
- [x] 模型版本和回滚通过；
- [x] 架构、功能、使用、测试和交付文档齐全。

### 11.2 项目集成验收

- [ ] 现场 SQL/Influx 部署完成；
- [ ] PLC 点位和时间戳确认；
- [ ] 规则清单签字；
- [ ] Profile 清单签字；
- [ ] 数据目录 ACL 和备份完成；
- [ ] 上位系统和报警推送联通；
- [ ] 故障恢复演练完成。

### 11.3 现场投产验收

- [ ] 影子运行达到约定周期；
- [ ] 误报率达到门槛；
- [ ] 已知故障识别率达到门槛；
- [ ] 活动异常和恢复流程闭环；
- [ ] 不影响 PLC 安全和 WCS 调度；
- [ ] 运维人员完成培训；
- [ ] 回退演练通过；
- [ ] 项目各方联合签署。

## 12. 已知限制和遗留项

1. 当前 Isolation Forest 主要针对固定窗口特征，不是深度时序神经网络；
2. 模型不能自动判断训练数据是否真实正常；
3. 合成 CI 准确率不能代替真实现场准确率；
4. 不同设备和工况需要独立 Profile 或模型；
5. 机器学习异常解释是特征贡献提示，不是自动根因证明；
6. 整机断电耐久仍依赖文件系统和硬件；
7. 当前管理 API 需要由部署网络和认证体系进一步限制；
8. 自动模型漂移、候选模型影子对比和训练审批工作流属于后续能力。

## 13. 运维责任划分

| 角色 | 责任 |
|---|---|
| WCS 研发 | 代码缺陷、模型框架、版本兼容和自动化测试 |
| 实施 | 配置、目录、部署、连接和现场联调 |
| PLC/设备 | 点位、采样含义、联锁和设备故障确认 |
| 工艺/生产 | 正常工况、阈值、训练数据和误报判断 |
| 运维 | 服务、数据库、磁盘、备份、监控和回退 |
| 项目负责人 | 版本冻结、验收门槛、审批和签署 |

## 14. 交付签署模板

| 项目 | 内容 |
|---|---|
| 项目名称 |  |
| WCS 版本/commit |  |
| 配置版本 |  |
| 规则版本 |  |
| Profile 版本 |  |
| 活动模型版本 |  |
| SQL Server 版本 |  |
| InfluxDB 版本 |  |
| 交付日期 |  |
| 遗留问题 |  |
| 回退版本 |  |

| 角色 | 姓名 | 日期 | 签字 |
|---|---|---|---|
| 研发负责人 |  |  |  |
| 测试负责人 |  |  |  |
| 实施负责人 |  |  |  |
| PLC/设备负责人 |  |  |  |
| 工艺/生产负责人 |  |  |  |
| 运维负责人 |  |  |  |
| 项目负责人 |  |  |  |
