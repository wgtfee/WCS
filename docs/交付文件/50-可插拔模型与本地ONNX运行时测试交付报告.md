# 可插拔模型与本地 ONNX 运行时测试交付报告

## 1. 范围

本报告覆盖 AnomalyEngine v3.8 的模型适配契约、本地文件治理、ONNX Runtime CPU 推理、Profile 分流、异常生命周期、SQL Candidate、版本激活、重启恢复、失败隔离和完整回归。

## 2. 测试原则

- 模型由 CI 现场生成，不把测试 ONNX 提交到仓库；
- 不使用远程模型下载；
- 不使用 GPU；
- 不跳过 SHA、维度、特征顺序、内存或 native 链接检查；
- 外部模型测试不得修改旧 Isolation Forest 的既有断言；
- 最终验收只认 latest exact head 的完整矩阵。

## 3. Core 契约测试

`PlcMlModelAdapterTests` 已验证 ManifestHash、FeatureSchema、Profile/Manifest 特征顺序、重复特征、路径穿越、Artifact SHA-256、分数变换、AdapterKind 唯一注册和未知 Adapter 拒绝。

## 4. 首轮完整矩阵

```text
Exact Head: 1a3cbdf2ff29e589f02f6581dfc9ff00ab9af9e0
Result: 20/20 success
```

| 工作流 | 运行号 | 结论 |
|---|---:|---|
| WCS PLC ML Model Adapter Compile | 28 | success |
| WCS PLC ML Model Adapter E2E | 21 | success |
| WCS PLC ML Model Adapter Host E2E | 11 | success |
| WCS PLC Anomaly ML | 149 | success |
| WCS PLC Anomaly ML E2E | 141 | success |
| WCS PLC Anomaly ML Governance | 102 | success |
| WCS PLC Anomaly ML Context Peer | 90 | success |
| WCS PLC Anomaly ML Version Throughput | 117 | success |
| WCS PLC Anomaly Engine Load | 248 | success |
| WCS PLC Anomaly Engine Soak | 231 | success |
| WCS Windows CI | 310 | success |
| WCS End-to-End Load | 236 | success |
| WCS One Hour Soak Load | 202 | success |
| WCS Transport Cycle Analysis | 99 | success |
| WCS Anomaly Health Scoring | 113 | success |
| WCS Anomaly Health Scoring SQL | 83 | success |
| WCS Asset Health Governance | 57 | success |
| WCS Asset Health Root Cause | 40 | success |
| WCS Asset Health Maintenance Compile | 34 | success |
| WCS Asset Health Maintenance | 41 | success |

## 5. v3.8 专项证据

### 5.1 Adapter Compile

```text
Workflow: WCS PLC ML Model Adapter Compile #28
Run ID: 30358668837
Artifact: wcs-plc-ml-model-adapter-compile-28
Digest: sha256:786b53a405e89c261ee2b8daad6082147f956dc84c336862af362f6e43072d7c
```

验证干净 Restore、Core Tests、Host Build、ONNX managed DLL 和 Linux native 资产。

### 5.2 真实 ONNX E2E

```text
Workflow: WCS PLC ML Model Adapter E2E #21
Run ID: 30358668940
Artifact: wcs-plc-ml-model-adapter-e2e-21
Digest: sha256:191e97b5e299e082ba062d007b776b93073c6795c94fc1e48f0442172d12ce31
```

已验证 CI 现场生成模型、ONNX Checker、CPU native Runtime、正常分数低于 0.2、异常分数高于 0.8、20,000 次推理、吞吐大于 1,000/s、RSS 增长不超过 256 MB、Hash/Shape/FeatureOrder 错误拒绝、本地版本激活、Store/Runtime 重建恢复和控制写入为 0。

### 5.3 Host 生命周期 E2E

```text
Workflow: WCS PLC ML Model Adapter Host E2E #11
Run ID: 30358668818
Artifact: wcs-plc-ml-model-adapter-host-11
Digest: sha256:28e948d8aba200d8fc09cf3ed141f0d825ba76cdc8f9a81a8bd0be21953402f2
```

已验证：

- v1/v2 本地已审批 Manifest；
- 初始加载 v1；
- 正常窗口 Prediction=6、Raise=0；
- 异常窗口 Prediction=6、Raise=2、Active=2；
- 活动异常期间 v2 激活返回 409；
- 恢复窗口 Prediction=6、Recover=2、Active=0；
- SQL Candidate=2、Recovered=2、ModelVersion=1；
- 恢复后切换 v2；
- Host 重启保持 v2；
- 坏 Hash 时 Host 健康、模型拒载并暴露失败；
- 恢复正确 Manifest 后 v2 再次加载；
- 无 PLC、设备、任务或调度写入。

## 6. 验收结果

- [x] latest exact head 专项编译成功；
- [x] latest exact head real ONNX E2E 成功；
- [x] latest exact head Host 生命周期 E2E 成功；
- [x] Core FeatureSchema 契约成功；
- [x] 旧 ML、Governance、Context、Version 和吞吐无回归；
- [x] Windows、Load、Soak、Health、Root Cause、Maintenance 全绿；
- [x] One Hour Soak 成功；
- [x] Production 默认关闭；
- [x] 文档 00、21、39、49～51 同步；
- [ ] 本证据提交形成的新 exact head 再次 20/20；
- [ ] PR Ready 后 Squash 合入 develop。

## 7. 当前结论

首轮源码与文档基线 `1a3cbdf2ff29e589f02f6581dfc9ff00ab9af9e0` 已达到 20/20。该结论仅代表仓库级测试，不代表真实模型准确率、许可证、现场容量、权限或正式投产已验收。本证据提交产生的新 Head 必须再次通过相同完整矩阵。