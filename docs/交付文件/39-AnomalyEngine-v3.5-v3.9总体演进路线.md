# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断与人工辅助决策路线。

```text
EMS/RGV 统一调度主线：第一阶段～第十二阶段，软件研发已经结束
AnomalyEngine 诊断扩展线：v1～v3.9，继续以只读诊断和人工辅助决策演进
```

统一调度后续不再使用“第十三阶段”扩展。AnomalyEngine 不得绕过既有状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

## 2. 已完成和当前基线

| 版本 | 能力 | 状态 |
|---|---|---|
| v1 | 规则、阈值、变化率、持续时间、MAD、一致性 | 已完成 |
| v2 | 特征窗口与 Isolation Forest | 已完成 |
| v2.1 | 模型治理、Shadow、Canary、审批、漂移与回滚 | 已完成 |
| v3.1 | 运行上下文与同群偏离 | 已完成 |
| v3.2 | 运输动作周期与阶段异常 | 已完成 |
| v3.3 | 多模型异常证据融合 | 已完成 |
| v3.4 | 可解释健康评分、趋势、SQL 历史与重启恢复 | 已完成，`develop@1d060983671d204fb25e4bf21d5b4bebd2596153` |
| v3.5 | 健康事件治理、MES Outbox、审计和重启恢复 | 已完成，`develop@ed74768a6338aff5a1c63409ab7fb0e344ca701c` |
| v3.6 | 根因关联、传播路径、SQL 快照和人工复核 | 已完成，`develop@900353ef8f17b3ab38cb4e711529c3fcc3629892` |
| v3.7 | 维修建议、反馈闭环、工单、指标和标签候选 | 已完成，`develop@f26cdbeae77c4e246477ea0c6ba60c3511ef86f4` |
| v3.8 | 可插拔模型、本地 ONNX Runtime、Profile 分流和失败隔离 | 功能、专项和文档已实现，正在进行完整矩阵与最终证据复验 |

## 3. 总体数据流

```text
规则 / 统计 / Isolation Forest / ONNX / 同群 / 周期异常
                         ↓
                 v3.3 Evidence Fusion
                         ↓
              v3.4 Health Score + Trend
                         ↓
       v3.5 Governed Health Event + MES Outbox
                         ↓
       v3.6 Root Cause Graph + Review Journal
                         ↓
       v3.7 Maintenance Rules + Feedback Journal
                         ↓
          v3.9 Failure Probability and RUL
```

v3.8 是 ML Evidence 入口的运行时扩展，不位于 v3.7 之后的控制链路。它可以独立向 Fusion 提供 Evidence，并复用后续健康、根因和维修反馈能力。

v3.5～v3.9 均属于诊断与辅助决策层。自动停机、自动修改调度、自动释放路权或自动下发维修动作必须进入独立安全项目。

## 4. v3.5：健康事件治理与 MES 联动

已交付事件创建/恢复、同一资产单活动事件、SQL Journal 双重幂等、MES HTTP Outbox、指数退避、DeadLetter、人工重试和 Host 重启恢复。SQL、MES 和网络故障不阻塞 PLC、任务和调度。

边界：不停止设备、不取消任务、不修改车辆、路径或路权、不替代 AlarmCenter、不把 MES 响应当作 PLC 联锁。

## 5. v3.6：根因关联与异常传播分析

已交付版本化审批图、GraphHash、图校验、有界关联、上游搜索、传播路径、可解释排序、确定性 AnalysisId、SQL 不可变快照、人工复核 Journal 和重启恢复。

```text
Merge SHA: 900353ef8f17b3ab38cb4e711529c3fcc3629892
Artifact: wcs-asset-health-root-cause-9
Digest: sha256:44688aa44d8710b24c1372b9dcf0dccc53f9e7165e94353d7ed034f8860194eb
```

边界：Confidence 不是故障概率；无图映射不猜测；Supplemented 不修改活动图；图修改必须新版本审批；不自动停机、取消任务或修改调度。

## 6. v3.7：维修决策支持与反馈闭环

已实现：

- 已审批 RuleSet、RuleSetHash 和同版本不同 Hash 拒绝；
- 根因到检查项、部件、工具、备件和安全说明映射；
- Confirmed/Supplemented 后生成建议；
- 无匹配规则不生成伪建议；
- 确定性 RecommendationId 和 SQL 幂等；
- Accepted、Rejected、FalsePositive、Repaired、NoFaultFound、Cancelled；
- MES 工单、处理人、完成时间和维修前后健康分；
- 接受率、确认故障率、误报率和关闭时长；
- 候选训练标签及独立人工审批；
- Host 重启恢复。

```text
Specialty: WCS Asset Health Maintenance #9
Artifact: wcs-asset-health-maintenance-9
Digest: sha256:26e1269d6057a0df3ca2a75be1191ccc771293bc09f53718b8a8cf459339a39b
Final matrix: 19/19
Merge SHA: f26cdbeae77c4e246477ea0c6ba60c3511ef86f4
```

边界：建议不是控制命令；MES 工单只是审计字段；标签 Approved 不自动训练或替换模型；不写 PLC、不停机、不取消任务、不修改调度。

## 7. v3.8：可插拔模型与本地 ONNX 运行时

### 7.1 目标

保留纯 .NET Isolation Forest 的训练和治理链路，引入统一 Adapter，使本地、已审批的 ONNX 模型复用现有 Profile、FeatureWindow、Shadow/Canary/Active、Candidate SQL、EventBus、Fusion 和恢复生命周期。

### 7.2 已实现能力

- `PlcMlModelManifest`：版本、Adapter、Artifact SHA、来源、审批、FeatureSchema、归一化、张量和阈值；
- `IPlcMlModelAdapter` / `IPlcMlModelRuntime`；
- `PlcMlModelAdapterRegistry`；
- `IPlcMlExternalModelStore` 与本地文件实现；
- Isolation Forest Adapter；
- Microsoft ONNX Runtime CPU Adapter；
- float32 输入输出和 `[1|-1, featureCount]` 校验；
- Profile→FeatureSchema 确定性映射；
- 相对路径、目录穿越、256 MB、SHA-256 校验；
- InferenceSession 按活动版本缓存和显式释放；
- `PluggablePlcMlAnomalyEngine` Decorator；
- DI 物理排除外部 Profile，避免双推理；
- 外部 Profile 禁止在线训练和 AutoTrain；
- Shadow、Canary、Active、连续异常/恢复；
- Candidate SQL、Detected/Recovered Event；
- 活动异常期间禁止切版；
- 本地版本激活和 Host 重启恢复；
- 坏 Hash 时 Host 健康、模型拒载、失败可见；
- 只读 Adapter 状态 API；
- 通用和 Production 双重默认关闭。

### 7.3 专项

`WCS PLC ML Model Adapter Compile`：干净 Restore、Core Tests、Host Build 和 ONNX managed/native 包资产。

`WCS PLC ML Model Adapter E2E`：CI 现场生成 ReduceMean+Sigmoid ONNX，验证 native 推理、正常/异常分数、20,000 次推理、吞吐、256 MB RSS、Hash/Shape/FeatureOrder 拒绝和重启恢复。

开发期成功证据：

```text
Workflow: WCS PLC ML Model Adapter E2E #10
Run ID: 30357291058
Head: 55c62b15bb28976c62f1f97e4dd2a56b95dffd8f
Artifact: wcs-plc-ml-model-adapter-e2e-10
Digest: sha256:fb6b261663f8b13b27297fd532f71dff578fc1dc09b7d58ecdaa8c59a90c098c
```

`WCS PLC ML Model Adapter Host E2E`：验证 RawSignal→ONNX→Candidate SQL→EventBus、正常/异常、Raise/Recover、切版阻断、恢复后激活、重启、坏 Hash 隔离和恢复。

最终 exact-head 运行号和 Artifact 在完整矩阵后回填。

### 7.4 安全边界

- 仓库不保存真实模型、URL、密钥和许可证；
- 不自动下载、训练、审批或激活外部模型；
- 模型输出是诊断分数，不是实际故障概率；
- Adapter 失败不阻塞 Host 和控制链路；
- 无有效模型时不产生预测；
- 不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单；
- 真实模型、数据、许可证、准确率和正式投产属于项目级验收。

### 7.5 完成门槛

- Core Manifest / FeatureSchema 测试通过；
- Adapter Compile、real ONNX E2E、Host E2E 通过；
- 旧 ML、Governance、Context、Version、Windows、Load、Soak、Health、RootCause、Maintenance 无回归；
- latest exact head 完整矩阵全绿；
- 文档 00、21、39、49～51 完整；
- 最终证据提交后再次 exact-head 全绿；
- PR #31 Ready 并 Squash 合入 develop。

## 8. v3.9：故障概率与剩余寿命预测

在具备长期退化数据、真实故障和维修记录后，输出部件未来故障概率、剩余寿命区间和建议维护窗口。

数据门槛：

- 足够长的连续历史；
- 明确失效时间；
- 部件更换和维修记录；
- 工况上下文；
- 可信标签；
- 足够真实失效样本；
- 时间切分、数据泄漏检查、校准和覆盖率评估。

数据不足时保持关闭，不输出虚假 RUL。

## 9. 执行顺序

```text
v3.5 已完成并合入 develop
  ↓
v3.6 已完成并合入 develop
  ↓
v3.7 已完成并合入 develop
  ↓
v3.8 已完成功能与专项，正在最终矩阵和证据收口
  ↓
v3.9 仅在真实失效和维修数据满足条件时实施
```

## 10. 统一安全原则

1. 默认关闭；
2. 输入、容量、时间窗口、队列、查询、模型大小和保留期有界；
3. 结果可解释、可版本化、可审计、可回退；
4. SQL、MES 或模型故障不阻塞控制链路；
5. 人工操作保存身份、原因和时间；
6. 不在仓库保存现场密钥、真实点位、维修规则、真实模型和未脱敏数据；
7. 不直接写 PLC、不自动停机、不修改调度；
8. 自动控制联动必须另立安全项目、风险分析和现场验收。
