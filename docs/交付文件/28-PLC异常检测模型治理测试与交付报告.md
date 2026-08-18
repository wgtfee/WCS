# PLC 异常检测模型治理测试与交付报告

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 能力 | AnomalyEngine v2.1 模型治理、影子运行和灰度发布 |
| 文档版本 | V1.0 |
| 功能基线 | `develop@8410fd82629b02f24b50ec05dc1a186ae6379d4c` |
| 测试环境 | GitHub Actions、Release Host、临时 SQL Server、模拟 RawSignalEvent |
| 交付状态 | 软件研发和自动化测试完成 |
| 非交付声明 | 不代表任一具体现场已经完成真实设备投产验收 |

## 2. 交付能力

本阶段交付：

1. `Disabled / Shadow / Canary / Active` 发布模式；
2. Shadow 候选与正式异常生命周期分离；
3. 确定性设备灰度分流；
4. 不可变训练数据集快照；
5. 按数据集版本训练；
6. 模型 Pending、Approved、Rejected 状态；
7. 训练申请人与批准人分离；
8. 模型审批、拒绝和激活审计；
9. 人工候选复核；
10. Precision 统计；
11. 在线异常分数漂移；
12. 校准均值和校准 P95 固化；
13. 在线训练污染保护；
14. 功能关闭时不自动创建治理表；
15. 管理 API 独立开关。

## 3. 代码交付清单

### 3.1 Core

- `PlcMlGovernanceModels.cs`；
- `PlcMlGovernanceService.cs`；
- `PlcMachineLearningModels.cs` 扩展；
- `PlcMlAnomalyEngine.cs` 治理分流；
- `IsolationForest.cs` 校准漂移基线。

### 3.2 Infrastructure

- `SqlSugarPlcMlGovernanceStore.cs`；
- `PlcMlGovernanceSchemaService.cs`；
- `FilePlcMlTrainingStore.cs` 数据集版本；
- `FilePlcMlModelStore.cs` 保存和激活分离；
- `PlcMlDependencyInjection.cs` 条件 Schema 初始化。

### 3.3 Host

- `PlcMlGovernanceController.cs`；
- `PlcMlAnomalyController.cs` 数据集训练参数；
- `appsettings.MlLoadTest.json` 治理测试配置。

### 3.4 Tests / CI

- `PlcMachineLearningTests.cs`；
- `PlcMlGovernanceTests.cs`；
- `PlcMlTrainingContaminationTests.cs`；
- `.github/workflows/anomaly-ml-governance.yml`。

## 4. 数据库交付

### 4.1 Wcs_PlcMlCandidate

保存：

- 候选 ID 和 Key；
- Profile 和模型版本；
- 发布模式；
- 是否进入正式生命周期；
- PLC、设备、窗口时间；
- 分数、阈值和解释；
- 活动/恢复状态；
- 人工复核结果、人员、时间和意见。

### 4.2 Wcs_PlcMlModelGovernance

保存：

- Profile 和模型版本；
- 数据集版本；
- Pending/Approved/Rejected；
- 训练申请人和时间；
- 审批人、时间和意见；
- 训练、校准样本数；
- 决策阈值。

### 4.3 Wcs_PlcMlDriftSnapshot

保存：

- Profile 和模型版本；
- 计算时间和样本数；
- 当前平均分和 P95；
- 校准平均分和 P95；
- DriftRatio；
- Stable/Warning/Critical。

## 5. 单元测试结果

工作流：`WCS PLC Anomaly ML #46`

通过范围：

- Core、Infrastructure、Host 编译；
- Isolation Forest 正常/异常区分；
- 特征顺序和窗口聚合；
- 正式生命周期激活和恢复；
- Shadow 只写候选、不发布正式事件；
- 活动异常期间禁止切换模型；
- 未审批模型禁止激活；
- 独立审核后允许激活；
- 训练申请人不能批准自己的模型；
- 活动模型默认阻止在线窗口追加；
- 显式受控再采集允许追加。

结果：全部通过，0 测试失败。

## 6. 治理端到端测试

工作流：`WCS PLC Anomaly ML Governance #9`

### 6.1 数据集冻结

| 指标 | 结果 |
|---|---:|
| 正常训练窗口 | 4,000 |
| 冻结数据集窗口 | 4,000 |
| 数据集版本 | 成功生成 |
| 特征哈希 | 成功生成 |
| 原子数据文件 | 成功生成 |
| 元数据文件 | 成功生成 |

### 6.2 待审批训练

| 指标 | 结果 |
|---|---:|
| 训练样本 | 4,000 |
| 建森林样本 | 3,200 |
| 校准样本 | 800 |
| 树数量 | 120 |
| 模型状态 | Pending |
| 训练后自动激活 | 否 |
| 未审批时 active.json | 不产生新活动模型 |

模型文件成功保存校准集平均分和 P95，用作漂移真实基线。

### 6.3 独立审批

| 项目 | 结果 |
|---|---|
| 申请人 | `governance-ci` |
| 审批人 | `independent-reviewer` |
| 是否同一人 | 否 |
| 审批状态 | Approved |
| 审批后激活 | 成功 |
| Host 状态模型版本 | 与批准版本一致 |

单元测试另验证申请人自批会返回冲突，且不会调用激活。

### 6.4 Shadow 运行

注入 100 台异常设备及恢复窗口：

| 指标 | 结果 |
|---|---:|
| Shadow 候选 | 100 |
| 候选恢复 | 100 |
| 候选最终活动 | 0 |
| 正式 `Wcs_PlcAnomaly` 行数 | 0 |
| AlarmCenter 正式生命周期 | 未路由 |
| 推理失败 | 0 |

Shadow 模式成功证明模型可以在生产数据上观察而不改变正式报警行为。

### 6.5 人工复核和效果统计

测试对 100 条候选执行：

| 复核结果 | 数量 |
|---|---:|
| True Positive | 60 |
| False Positive | 40 |
| 已复核 | 100 |
| 未复核 | 0 |
| Precision | 0.60 |

该数值是治理功能的测试输入，不代表现场模型真实精度。

### 6.6 漂移

最终漂移证据：

| 指标 | 结果 |
|---|---:|
| 在线分数窗口 | 500 |
| 校准平均分 | 约 0.51405 |
| 校准 P95 | 约 0.55445 |
| 当前平均分 | 约 0.59197 |
| 当前 P95 | 约 0.64279 |
| DriftRatio | 约 0.15932 |
| 状态 | Warning |

该结果证明漂移状态使用模型真实校准分布计算并持久化。

### 6.7 Canary

Host 重启后配置 25% Canary，注入 100 台异常设备：

| 指标 | 结果 |
|---|---:|
| 候选总数 | 100 |
| 正式路由 | 34 |
| 影子路由 | 66 |
| 物理正式 SQL 行数 | 34 |
| 正式 SQL 与路由守恒 | 是 |
| 全部恢复 | 是 |
| 推理失败 | 0 |

由于路由使用稳定哈希，100 台测试设备的实际命中可以不是严格 25，但必须落在测试门槛范围内，并且重启后保持确定性。

### 6.8 训练污染保护

| 阶段 | features.jsonl 行数 |
|---|---:|
| 初始正常采集完成 | 4,000 |
| Shadow 异常及恢复后 | 4,000 |
| Canary 异常及恢复后 | 4,000 |

批准模型后注入的 600 个异常/恢复窗口没有追加到正常训练池。

## 7. 回归测试

同一功能提交完成以下回归：

### 7.1 ML 准确性 E2E #38

- 训练；
- 重启加载；
- 未见正常窗口；
- 异常激活；
- 恢复；
- SQL 精确生命周期；
- 内存门槛。

结果：通过。

### 7.2 ML Version Throughput #24

- 两个模型版本；
- 回滚；
- 重启保持活动版本；
- 100,000 窗口推理；
- 正式 SQL 零误记录；
- 内存门槛。

结果：通过。

### 7.3 v1 高基数 Load #82

- 213,000 PLC 样本；
- 2,000 阈值生命周期；
- 1,000 一致性生命周期；
- 200,000 正常样本零额外异常；
- SQL 精确计数；
- 状态完整清理。

结果：通过。

### 7.4 v1 10 分钟 Soak #65

| 指标 | 结果 |
|---|---:|
| PLC 样本 | 631,200 |
| 激活 | 7,200 |
| 恢复 | 7,200 |
| 正式 SQL | 7,200 |
| 活动残留 | 0 |
| 失败 | 0 |
| 初始 RSS | 约 159.05 MB |
| 峰值 RSS | 约 406.41 MB |
| 最终 RSS | 约 291.02 MB |
| 最终增长 | 约 131.97 MB |

结果：通过。

### 7.5 Windows CI #128

- Wcs.Core 编译和测试；
- Wcs.Host 编译；
- Wcs.Desktop 编译。

结果：全部通过。

### 7.6 系统端到端 Load #70

Release Host、临时 SQL、模拟 PLC、HTTP、SignalR 和持久化链路通过。

## 8. 安全设计验证

已验证：

- 默认发布模式 Shadow；
- 默认要求模型审批；
- 管理 API 默认关闭；
- ML 和管理 API 都关闭时不自动创建治理表；
- 自审批被拒绝；
- 被拒模型不能直接批准；
- 活动候选阻止模型切换；
- Shadow 不写正式异常；
- Canary 正式数量与 SQL 物理行数一致；
- 活动模型默认停止追加正常训练池；
- 模型特征不匹配禁止加载；
- 模型文件先验证再激活。

未包含的项目级安全工作：

- Controller 角色授权；
- SSO/身份提供者；
- API Gateway 策略；
- 网络 ACL；
- 审批人组织权限；
- 文件目录加密和备份；
- 现场操作审计对接。

这些必须在项目集成阶段完成。

## 9. 验收结论

### 9.1 软件研发验收

结论：通过。

依据：

- 功能实现完整；
- 单元测试通过；
- 真实 SQL 治理 E2E 通过；
- Shadow、Canary、审批、复核、漂移和污染保护通过；
- 原有 ML、v1、Windows 和系统负载回归通过；
- 文档和 CI 工作流已提交仓库。

### 9.2 项目集成验收

尚需：

- 接入项目身份和角色权限；
- 确认真实 Profile、信号单位和采样周期；
- 制定人工复核责任人；
- 配置模型、数据集和 SQL 备份；
- 连接真实 PLC 和现场数据；
- 完成影子运行报告。

### 9.3 现场投产验收

尚需：

- 真实正常工况覆盖；
- 真实故障和维修记录；
- 误报、漏报门槛签署；
- Canary 分阶段观察；
- AlarmCenter 联动确认；
- 回退演练；
- 生产、设备、工艺、运维和信息化联合签署。

## 10. 交付清单

- [x] 治理代码进入 `develop`；
- [x] SQL 治理实体和索引；
- [x] 冻结数据集；
- [x] 模型审批；
- [x] 双人原则；
- [x] Shadow；
- [x] Canary；
- [x] 人工复核；
- [x] 效果统计；
- [x] 漂移；
- [x] 训练污染保护；
- [x] 单元和 E2E 测试；
- [x] 回归测试；
- [x] 配置和操作文档；
- [ ] 项目认证与角色集成；
- [ ] 真实现场影子运行；
- [ ] 现场验收签署。

## 11. 后续阶段入口

第一阶段完成后，下一研发方向为：

```text
多信号设备级融合
+ 动作周期和状态序列
+ 运行上下文
+ 同类设备横向对比
```

深度学习仍不是下一步的默认实现。只有传统规则、统计、Isolation Forest 和周期模型无法覆盖长时序异常，并且现场已积累足够数据时，才评估 AutoEncoder、TCN、LSTM 或 Transformer。
