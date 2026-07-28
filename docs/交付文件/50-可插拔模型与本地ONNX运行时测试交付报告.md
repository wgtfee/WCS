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

`PlcMlModelAdapterTests` 验证：

1. ManifestHash 确定性；
2. FeatureSchema 确定性；
3. Profile 与 Manifest 特征顺序完全一致；
4. 重复 FeatureName 拒绝；
5. 绝对路径和目录穿越拒绝；
6. Artifact SHA-256 不一致拒绝；
7. Identity、Sigmoid、OneMinus 有界；
8. AdapterKind 唯一注册；
9. 未注册 Adapter 拒绝。

## 4. 编译专项

工作流：`WCS PLC ML Model Adapter Compile`

验证：

- 删除 bin/obj 后干净 Restore；
- Core Tests 编译；
- Adapter 契约测试；
- Host 干净编译；
- ONNX managed DLL；
- Linux x64 `libonnxruntime.so` NuGet 资产。

已知在开发阶段发现并修复：

- 嵌套 Runtime 错误引用外层实例导致 `CS0120`；
- `Directory.EnumerateFiles` 错误命名参数；
- 增量构建掩盖干净构建错误；
- 旧项目强制 RID publish 与历史 NModbus RID 包冲突，因此编译门禁改为普通 Host Build + 官方 NuGet native 资产检查，真实 native 加载由独立 E2E 负责。

## 5. 真实 ONNX Adapter E2E

工作流：`WCS PLC ML Model Adapter E2E`

CI 生成模型：

```text
Input:  features float32 [1,8]
Graph:  ReduceMean → Sigmoid
Output: score float32 [1,1]
Opset:  13
IR:     10
```

已验证：

- ONNX Checker；
- SHA-256；
- CPU native Runtime 加载；
- 正常分数约 0.119；
- 异常分数约 0.881；
- 20,000 次推理；
- 吞吐大于 1,000 次/秒；
- RSS 增长不超过 256 MB；
- 错误 Hash 拒绝；
- 错误 Shape 拒绝；
- 错误 FeatureOrder 拒绝；
- 本地版本激活；
- Store 和 Runtime 重建后分数一致；
- Linux x64 native 依赖 `ldd` 无 not found；
- 控制写入数为 0。

开发期成功证据：

```text
Workflow: WCS PLC ML Model Adapter E2E #10
Run ID: 30357291058
Head: 55c62b15bb28976c62f1f97e4dd2a56b95dffd8f
Artifact: wcs-plc-ml-model-adapter-e2e-10
Digest: sha256:fb6b261663f8b13b27297fd532f71dff578fc1dc09b7d58ecdaa8c59a90c098c
```

该次证据显示约 43,866 次/秒，20,000 次推理后的 RSS 增长约 18.5 MB。该结果仅代表 CI 微型模型和对应 runner，不作为现场模型容量承诺。

## 6. Host 生命周期 E2E

工作流：`WCS PLC ML Model Adapter Host E2E`

链路：

```text
RawSignalEvent
→ EventBus
→ PlcAnomalySample
→ Pluggable Runtime
→ ONNX Runtime
→ Wcs_PlcMlCandidate
→ Detected / Recovered Event
```

精确断言：

- 本地 v1、v2 两个 Manifest；
- 初始活动版本 v1；
- 正常 2 设备 × 3 窗口：Prediction=6、Raise=0；
- 异常 2 设备 × 3 窗口：Prediction=6、Raise=2、Active=2；
- 活动异常存在时 v2 激活返回 409；
- 恢复 2 设备 × 3 窗口：Prediction=6、Recover=2、Active=0；
- SQL Candidate=2、Recovered=2、ModelVersion=1；
- 恢复后 v2 激活成功；
- Host 重启后活动版本保持 v2；
- 篡改 active Manifest Hash 后 Host 仍健康，Runtime 拒载并暴露失败；
- 恢复正确 Manifest 并重启后 v2 再次加载；
- 无 PLC、设备、任务或调度写入。

首轮运行已通过模型加载、正常、异常、Raise、Recover 和切版阻断；SQL 脚本因字符串引号错误失败，已修正为专用数据库全表精确计数。最终成功运行号将在完整矩阵后回填。

## 7. 回归矩阵

v3.8 最终矩阵至少包含：

- Model Adapter Compile；
- Model Adapter E2E；
- Model Adapter Host E2E；
- Windows CI；
- End-to-End Load；
- One Hour Soak；
- PLC Anomaly Engine Load / Soak；
- PLC Anomaly ML；
- PLC Anomaly ML E2E；
- ML Governance；
- ML Context Peer；
- ML Version Throughput；
- Transport Cycle；
- Health Scoring / SQL；
- Asset Health Governance；
- Root Cause；
- Maintenance。

## 8. 验收门槛

- [ ] latest exact head 专项编译成功；
- [ ] latest exact head real ONNX E2E 成功；
- [ ] latest exact head Host 生命周期 E2E 成功；
- [ ] Core FeatureSchema 契约成功；
- [ ] 旧 ML、Governance、Context、Version 和吞吐无回归；
- [ ] Windows、Load、Soak、Health、Root Cause、Maintenance 全绿；
- [ ] Production 默认关闭；
- [ ] 文档 00、21、39、49～51 同步；
- [ ] 最终证据提交后再次进行 exact-head 完整矩阵；
- [ ] PR Ready 后 Squash 合入 develop。

## 9. 当前结论

v3.8 Adapter 和 native ONNX 基础专项已成功，Host 生命周期专项已验证主要业务链路并修复测试 SQL。仓库级最终结论需等待最新文档 Head 的完整矩阵和最终复验，不能用开发期旧 Head 替代。
