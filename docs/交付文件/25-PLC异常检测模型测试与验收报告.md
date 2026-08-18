# PLC 异常检测模型测试与验收报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 文档版本 | V1.0 |
| 测试对象 | PLC Telemetry、WAL、AnomalyEngine v1、Isolation Forest v2 |
| 最终功能基线 | `develop@1e7cda6b8d6dfd230293575edc5680bb383971e6` |
| 测试时间 | 2026-07-23 至 2026-07-25 |
| 测试环境 | GitHub Actions 隔离 Runner、临时 SQL Server、InfluxDB 2.7、模拟 PLC、Release Host |
| 结论 | 软件研发测试通过；仍需项目现场联调和投产验收 |

## 2. 测试目标

验证以下质量目标：

1. PLC 时序数据在正常、数据库故障、Host 强制退出和恢复后不发生不可解释丢失；
2. 规则、统计和一致性异常生命周期计数准确；
3. 大量正常数据不产生额外正式异常；
4. 高基数信号状态可以回收，不随历史设备数量无限增长；
5. Isolation Forest 可以训练、校准、落盘、重启加载、在线推理和回滚；
6. 机器学习正式异常必须经过连续窗口确认，恢复必须经过连续正常窗口；
7. 模型管理与推理并发安全；
8. 真实 `RawSignalEvent → EventBus → SampleFactory → ML Engine` 链路可用；
9. 新能力不破坏原有 WCS 调度、SignalR、SQL 和 Windows 构建。

## 3. 自动化工作流

| 工作流 | 文件 | 主要验证 |
|---|---|---|
| WCS PLC Telemetry Storage Load | `.github/workflows/telemetry-storage-load.yml` | SQL/Influx 20 万点精确计数、Influx 停机重放 |
| WCS PLC Telemetry WAL Crash | `.github/workflows/telemetry-wal-crash.yml` | 数据库停机、5 万点 WAL、`kill -9`、新 Host 恢复 |
| WCS PLC Anomaly Engine Load | `.github/workflows/anomaly-engine-load.yml` | 高基数、阈值、一致性、正常数据、SQL 精确生命周期 |
| WCS PLC Anomaly Engine Soak | `.github/workflows/anomaly-engine-soak.yml` | 10 分钟持续生命周期、误报、内存趋势 |
| WCS PLC Anomaly ML | `.github/workflows/anomaly-ml.yml` | Core/Infrastructure/Host 编译和 ML 单元测试 |
| WCS PLC Anomaly ML E2E | `.github/workflows/anomaly-ml-e2e.yml` | 真实事件链路训练、重启、正常集、异常、恢复和 SQL |
| WCS PLC Anomaly ML Version Throughput | `.github/workflows/anomaly-ml-version-throughput.yml` | 双版本、回滚、重启和 10 万窗口吞吐 |
| WCS End-to-End Load / Soak | `.github/workflows/e2e-load.yml`、`e2e-soak.yml` | WCS API、SignalR、调度、SQL 和 Host 存活 |

## 4. 单元和组件测试

### 4.1 v1 测试范围

- 通配符规则匹配；
- 上限、下限和变化率；
- 连续异常和连续恢复；
- Boolean true 持续时间；
- Median/MAD 基线；
- Running/Speed 跨信号一致性；
- 关联信号年龄；
- 活动异常保护；
- 恢复状态 TTL；
- 统计窗口按需创建；
- 设备快照和关联样本淘汰；
- 高基数状态完全清空。

### 4.2 v2 测试范围

- Isolation Forest 对明显离群样本的区分；
- 特征名称和顺序确定性；
- 数值窗口和布尔窗口计算；
- 训练集稳定排序；
- 80% 建模、20% 独立校准；
- 两级观察/正式阈值；
- 连续异常激活和连续正常恢复；
- 模型文件保存、加载和版本列表；
- 活动异常期间禁止切换模型；
- 回滚后活动模型更新；
- 管理 API 默认关闭；
- 推理与模型切换互斥。

### 4.3 通过标准

- Core、Infrastructure 和 Host 编译 0 错误；
- 单元测试 0 失败；
- 测试无未处理异常；
- 相同固定训练集结果可重复。

最终 ML 单元工作流通过。

## 5. PLC 时序存储精确计数

### 5.1 SQL Server

| 指标 | 结果 |
|---|---:|
| 生成时序点 | 200,000 |
| 应用接收 | 200,000 |
| 应用持久化 | 200,000 |
| SQL 物理行数 | 200,000 |
| dropped | 0 |
| conservationDelta | 0 |
| 最终 queue / spool / in-flight | 0 / 0 / 0 |

结论：正常 SQL Server 批量写入未发现数据丢失。

### 5.2 InfluxDB 正常与停机恢复

| 阶段 | 应用累计接收 | Influx 物理点数 | spool | dropped |
|---|---:|---:|---:|---:|
| 正常写入 | 200,000 | 200,000 | 0 | 0 |
| Influx 停机继续注入 | 220,000 | 200,000 | 20,000 | 0 |
| Influx 恢复重放 | 220,000 | 220,000 | 0 | 0 |

结论：Provider 故障期间数据进入本地 spool，恢复后物理点数守恒。

## 6. WriteAhead 强制崩溃测试

### 6.1 测试步骤

1. 启动 Host；
2. 停止目标数据库；
3. 注入 50,000 个时序点；
4. 确认全部完成 WAL 刷盘并被接收；
5. 对 Host 执行 `kill -9`；
6. 恢复数据库；
7. 启动全新 Host；
8. 自动扫描并重放 WAL；
9. 查询物理数据库精确计数。

### 6.2 SQL Server 结果

| 指标 | 结果 |
|---|---:|
| WAL 已确认接收 | 50,000 |
| 崩溃前数据库写入 | 0 |
| 崩溃前 WAL 待处理 | 50,000 |
| 新 Host 恢复 | 50,000 |
| SQL 物理新增 | 50,000 |
| dropped | 0 |
| 最终 WAL | 0 |
| conservationDelta | 0 |

### 6.3 InfluxDB 结果

| 指标 | 结果 |
|---|---:|
| WAL 已确认接收 | 50,000 |
| 崩溃前数据库写入 | 0 |
| 崩溃前 WAL 待处理 | 50,000 |
| 新 Host 恢复 | 50,000 |
| Influx 物理新增 | 50,000 |
| dropped | 0 |
| 最终 WAL | 0 |
| conservationDelta | 0 |

结论：进程级强制终止场景下，WriteAhead 已确认接收的数据可以恢复。整机断电仍依赖文件系统和存储硬件的断电保护能力。

## 7. v1 高基数突发测试

### 7.1 负载组成

- 2,000 个阈值异常生命周期；
- 1,000 个跨信号一致性生命周期；
- 200,000 个正常样本；
- 总 PLC 样本 213,000；
- 并发事件发布和真实 SQL 生命周期持久化。

### 7.2 结果

| 指标 | 结果 |
|---|---:|
| 处理速度 | 约 17,852 样本/秒 |
| 正式激活 | 3,000 |
| 正式恢复 | 3,000 |
| SQL 生命周期 | 3,000 |
| SQL 已恢复 | 3,000 |
| SQL 活动中 | 0 |
| 重复 AnomalyKey | 0 |
| 正常样本额外异常 | 0 |
| 检测失败 | 0 |
| suppressed / dropped | 0 |

### 7.3 高基数内存治理

修复前，约 3,100 个不同信号状态导致 RSS 增长约 667 MB。

修复后：

| 指标 | 结果 |
|---|---:|
| 最终规则状态 | 0 |
| 最终统计窗口 | 0 |
| 最终设备快照 | 0 |
| 最终关联样本 | 0 |
| 淘汰规则状态 | 3,100 |
| 淘汰关联样本 | 2,000 |
| 淘汰设备快照 | 1,000 |
| RSS 增长 | 约 142 MB |

结论：已恢复且超过 TTL 的高基数状态可完全回收，业务生命周期计数保持准确。

## 8. v1 十分钟持续测试

| 指标 | 结果 |
|---|---:|
| 持续时间 | 10 分钟 |
| PLC 样本 | 631,200 |
| 正式激活 | 7,200 |
| 正式恢复 | 7,200 |
| SQL 生命周期 | 7,200 |
| 阈值异常 | 4,800 |
| 一致性异常 | 2,400 |
| 最终活动异常 | 0 |
| 检测失败 | 0 |
| dropped / suppressed | 0 |
| 最后两分钟 RSS 斜率 | 负值，未持续爬升 |

最新最终提交的 `WCS PLC Anomaly Engine Soak` 构建、负载、SQL、内存趋势和日志检查全部通过。

## 9. v2 训练与准确性 E2E

### 9.1 真实链路

最终 E2E 不直接调用 ML 引擎，而是使用：

```text
RawSignalEvent
→ EventBus
→ PlcMlAnomalyBackgroundService
→ PlcAnomalySampleFactory
→ PlcFeatureWindowEngine
→ PlcMlAnomalyEngine
→ PlcAnomalyRaised/RecoveredEvent
→ SQL
```

### 9.2 训练数据

| 指标 | 结果 |
|---|---:|
| 正常训练窗口 | 4,000 |
| 建森林样本 | 3,200 |
| 独立校准样本 | 800 |
| TreeCount | 120 |
| SampleSize | 256 |
| 特征维数 | 8 |
| 原始训练点处理速度 | 约 20,523 点/秒 |

训练完成后验证：

- `active.json` 存在；
- 模型 Profile 正确；
- 树数量正确；
- 特征顺序正确；
- 模型 SHA256 可生成；
- Host 停止并重启后加载同一活动版本。

### 9.3 未见正常集

| 指标 | 结果 |
|---|---:|
| 未参与训练设备 | 100 |
| 正常窗口 | 1,000 |
| 正式异常 | 0 |
| SQL 正常设备异常行 | 0 |
| 推理失败 | 0 |

### 9.4 异常和恢复集

| 指标 | 结果 |
|---|---:|
| 异常设备 | 100 |
| 异常窗口 | 300 |
| 激活 | 100/100 |
| 恢复窗口 | 300 |
| 恢复 | 100/100 |
| SQL 生命周期 | 100 |
| SQL 已恢复 | 100 |
| SQL 活动中 | 0 |
| 重复 AnomalyKey | 0 |
| 涉及活动模型版本数 | 1 |

结论：在当前合成但未见的正常/异常分布上，正式误报为 0，异常识别和恢复均为 100%。该结果不能替代现场真实数据准确率评估。

## 10. 模型版本和 10 万窗口吞吐

### 10.1 版本管理

测试执行：

1. 同一训练集连续训练两个版本；
2. 版本列表仅一个版本标记为活动；
3. 回滚到第一版本；
4. 停止并重启 Host；
5. 确认重启后仍加载回滚版本。

结果：全部通过。

### 10.2 全链路吞吐

| 指标 | 结果 |
|---|---:|
| 设备数 | 1,000 |
| 每设备窗口 | 100 |
| 总窗口 | 100,000 |
| 每窗口原始点 | 3 |
| 总 RawSignalEvent | 300,000 |
| 完成窗口 | 100,000 |
| 推理次数 | 100,000 |
| dropped incomplete window | 0 |
| 正式异常 | 0 |
| SQL 误记录 | 0 |
| 推理失败 | 0 |
| 全链路处理速度 | 约 50,013 原始点/秒 |
| RSS 增长 | 约 133 MB |

此结果包含 EventBus 和 SampleFactory 开销，不是仅调用模型算法的理想化吞吐。

## 11. WCS 回归测试

异常模型和时序存储加入后，原有 WCS 回归覆盖：

- Core 测试；
- Host 构建；
- Desktop 构建；
- HTTP 并发；
- SignalR 连接和消息；
- 调度队列 Drain；
- SQL 持久化；
- Host 存活；
- OOM、未处理异常和连接错误日志。

历史最终回归中 HTTP、SignalR 和调度链路均无失败，Windows Core/Host/Desktop 构建通过。

## 12. 验收判定

### 12.1 软件研发验收：通过

通过条件：

- 所有最终工作流成功；
- 精确计数守恒；
- 正常数据无额外正式异常；
- SQL 无重复生命周期；
- Host 无未处理异常和 OOM；
- 模型版本可回滚；
- 真实事件链路通过；
- 文档和配置说明齐全。

### 12.2 集成验收：待项目执行

必须补充：

- 实际 PLC 点位和时间戳；
- 实际 SQL/Influx 部署；
- 真实设备类型和工况 Profile；
- 模型目录和权限；
- MES/WMS 任务上下文；
- 告警推送和运维平台联通。

### 12.3 现场验收：待项目执行

至少包括：

- 真实正常数据影子运行；
- 误报率、漏报率和平均确认时间；
- 不同产品、负载、班次和环境条件；
- 已知故障复现；
- 设备工程师和工艺人员确认；
- 报警升级、人工处置和恢复；
- 数据库断线、Host 重启和磁盘告警演练；
- 上线和回退联合签署。

## 13. 证据保留

CI 产物至少保留：

- Host 日志；
- 训练、正常、异常和恢复响应 JSON；
- 模型文件和 SHA256；
- SQL 精确计数；
- telemetry/WAL 守恒结果；
- RSS 采样和趋势；
- 工作流运行编号和 commit SHA。

关键产物名称示例：

- `wcs-plc-anomaly-ml-e2e-28`；
- `wcs-plc-anomaly-ml-version-throughput-14`；
- `wcs-plc-anomaly-*`；
- `wcs-plc-telemetry-*`。

GitHub Actions 产物存在保留期限，正式项目应下载到受控交付目录并生成校验值。