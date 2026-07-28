# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断与人工辅助决策路线。统一调度主线第一至第十二阶段已经结束；AnomalyEngine 不得绕过状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

## 2. 已完成和当前基线

| 版本 | 能力 | 状态 |
|---|---|---|
| v3.4 | 健康评分、趋势、SQL 历史 | 已完成 |
| v3.5 | 健康事件治理、MES Outbox、审计 | 已完成，`develop@ed74768a6338aff5a1c63409ab7fb0e344ca701c` |
| v3.6 | 根因关联、传播路径、人工复核 | 已完成，`develop@900353ef8f17b3ab38cb4e711529c3fcc3629892` |
| v3.7 | 维修建议、反馈、工单、指标和标签 | 已完成，`develop@f26cdbeae77c4e246477ea0c6ba60c3511ef86f4` |
| v3.8 | 可插拔模型、本地 ONNX Runtime、Profile 分流和失败隔离 | 功能、专项、文档和首轮 20/20 已完成，等待最终证据 Head 复验与合并 |
| v3.9 | 故障概率与剩余寿命 | 仅在真实失效数据满足条件后实施 |

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

v3.8 是 ML Evidence 入口扩展，可独立向 Fusion 提供 Evidence，不位于 v3.7 之后的控制链路。

## 4. v3.5～v3.7 摘要

- v3.5：健康事件创建/恢复、SQL Journal、MES Outbox、指数退避、DeadLetter 和重启恢复；
- v3.6：审批图、GraphHash、有界关联、传播路径、不可变快照、复核 Journal；
- v3.7：审批 RuleSet、RuleSetHash、维修建议、反馈 Journal、MES 工单字段、指标、候选训练标签。

这些阶段均为诊断与人工辅助决策，不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单。

## 5. v3.8：可插拔模型与本地 ONNX 运行时

### 5.1 已实现能力

- `PlcMlModelManifest` 与确定性 ManifestHash；
- Profile→FeatureSchema 精确映射；
- `IPlcMlModelAdapter` / `IPlcMlModelRuntime` / Adapter Registry；
- 本地 External Model Store；
- Isolation Forest Adapter；
- Microsoft ONNX Runtime CPU Adapter；
- float32 输入输出、张量 Shape、路径 containment、256 MB、SHA-256 校验；
- InferenceSession 缓存和显式释放；
- `PluggablePlcMlAnomalyEngine` Decorator；
- DI 物理排除外部 Profile，避免双推理；
- 外部 Profile 禁止在线训练和 AutoTrain；
- Shadow、Canary、Active、连续异常和恢复；
- Candidate SQL、Detected/Recovered Event；
- 活动异常期间禁止切版；
- 本地版本激活和 Host 重启恢复；
- 坏 Hash 时 Host 健康、模型拒载、失败可见；
- 只读 Adapter 状态 API；
- 通用和 Production 双重默认关闭。

### 5.2 首轮完整矩阵

```text
Exact Head: 1a3cbdf2ff29e589f02f6581dfc9ff00ab9af9e0
Result: 20/20 success
```

专项证据：

```text
Compile #28 / Run 30358668837
Artifact: wcs-plc-ml-model-adapter-compile-28
Digest: sha256:786b53a405e89c261ee2b8daad6082147f956dc84c336862af362f6e43072d7c

Real ONNX E2E #21 / Run 30358668940
Artifact: wcs-plc-ml-model-adapter-e2e-21
Digest: sha256:191e97b5e299e082ba062d007b776b93073c6795c94fc1e48f0442172d12ce31

Host E2E #11 / Run 30358668818
Artifact: wcs-plc-ml-model-adapter-host-11
Digest: sha256:28e948d8aba200d8fc09cf3ed141f0d825ba76cdc8f9a81a8bd0be21953402f2
```

Host 专项已验证正常不 Raise、异常 Raise=2、恢复 Recover=2、SQL 2/2/1、活动异常切版 409、恢复后 v2、重启恢复、坏 Hash 隔离和修复后恢复。

### 5.3 完成门槛

- [x] Core Manifest / FeatureSchema；
- [x] Adapter Compile、real ONNX E2E、Host E2E；
- [x] 旧 ML、Governance、Context、Version、Windows、Load、Soak、Health、RootCause、Maintenance 无回归；
- [x] 首轮 exact head 20/20；
- [x] 文档 00、21、39、49～51；
- [ ] 最终证据提交形成的新 exact head 20/20；
- [ ] PR #31 Ready 并 Squash 合入 develop。

### 5.4 安全边界

- 仓库不保存真实模型、URL、密钥和许可证；
- 不自动下载、训练、审批或激活外部模型；
- 模型输出是诊断分数，不是实际故障概率或安全联锁；
- Adapter 失败不阻塞 Host 和控制链路；
- 无有效模型时不产生预测；
- 不写 PLC、不停机、不取消任务、不修改路线、路权、车辆选择或派单；
- 真实模型、数据、许可证、准确率和正式投产属于项目级验收。

## 6. v3.9：故障概率与剩余寿命预测

仅在具备长期退化历史、明确失效时间、部件更换和维修记录、工况上下文、可信标签和足够真实失效样本后实施。必须完成时间切分、数据泄漏检查、校准和覆盖率评估；数据不足时保持关闭，不输出虚假 RUL。

## 7. 执行顺序

```text
v3.5 已完成并合入 develop
  ↓
v3.6 已完成并合入 develop
  ↓
v3.7 已完成并合入 develop
  ↓
v3.8 首轮 20/20 已完成，等待最终证据 Head 复验与合并
  ↓
v3.9 仅在真实失效和维修数据满足条件时实施
```

## 8. 统一安全原则

1. 默认关闭；
2. 输入、容量、窗口、队列、查询、模型大小和保留期有界；
3. 结果可解释、可版本化、可审计、可回退；
4. SQL、MES 或模型故障不阻塞控制链路；
5. 人工操作保存身份、原因和时间；
6. 仓库不保存现场密钥、真实点位、维修规则、真实模型和未脱敏数据；
7. 不直接写 PLC、不自动停机、不修改调度；
8. 自动控制联动必须另立安全项目、风险分析和现场验收。