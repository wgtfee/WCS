# AnomalyEngine v3.8 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | v3.8 |
| 能力 | 可插拔模型与本地 ONNX Runtime |
| 默认状态 | 关闭 |
| 首轮仓库验收 | `1a3cbdf2ff29e589f02f6581dfc9ff00ab9af9e0`，20/20 success |
| 现场投产 | 必须独立签署，不由仓库 CI 自动代表 |

## 2. 仓库安全默认

通用和 Production 配置均保持：

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": false,
      "ManagementApiEnabled": false,
      "PluggableRuntime": {
        "Enabled": false,
        "MaximumTrackedWindows": 5000,
        "InactiveStateRetentionSeconds": 300,
        "Profiles": []
      },
      "Profiles": []
    }
  }
}
```

仓库不包含真实 ONNX、现场 Manifest、下载 URL、密钥、许可证文件、真实 PLC 点位、资产编号或自动激活配置。

## 3. 生产启用顺序

```text
现有规则/统计/Isolation Forest 基线稳定
→ 确认本地模型许可证与运行环境
→ 冻结 Profile 与 FeatureSchema
→ 离线训练和回测
→ 导出 ONNX
→ 生成 SHA-256 与 Manifest
→ 模型/工艺/设备负责人审批
→ 发布到本地受控目录
→ MachineLearning.Enabled=true
→ PluggableRuntime.Enabled=true
→ 单 Profile Shadow
→ Candidate 与误报复核
→ Canary
→ Active
```

不得从关闭状态直接跳到 Active。

## 4. 生产约束

- 外部 Profile 必须唯一、`CollectTrainingData=false`、`AutoTrain=false`；
- Signals 顺序必须与离线训练一致；
- 初始 DeploymentMode 为 Shadow；
- Manifest 必须记录 ProfileId、Version、Adapter、ArtifactSha256、Source、审批、FeatureNames、归一化、张量、阈值、校准和外部训练/许可证编号；
- 同一 Version 不得对应不同 ArtifactSha256 或 FeatureSchema；
- 模型只能从受控本地目录加载；
- 发布账号与运行账号分离；
- 活动异常期间禁止切版；
- 无有效模型时不推理；
- Adapter、SQL 或模型失败不阻塞 Host 和控制链路。

## 5. 监控与回退

只读状态接口：

```text
GET /api/anomaly/ml/adapters/status
```

至少监控 ActiveAdapterId、ActiveModelVersion、ManifestHash、ArtifactSha256、Predictions、Raised、Recovered、Failures、ActiveAnomalies、TrackedInferenceStates、CompletedWindows、DroppedIncompleteWindows 和 LastError。

紧急关闭：

```text
AnomalyDetection__MachineLearning__PluggableRuntime__Enabled=false
```

关闭外部推理不会删除模型、Candidate 或审计记录，也不影响 PLC、任务、路权和调度。

## 6. 首轮专项证据

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

Host 专项已通过 v1/v2、本地加载、正常/异常、Raise=2、Recover=2、SQL 2/2/1、活动异常切版 409、恢复后 v2、重启恢复、坏 Hash 隔离和修复后恢复。

## 7. 仓库级验收

- [x] Core Manifest / FeatureSchema 测试；
- [x] Adapter Compile 干净构建；
- [x] 真实 ONNX native E2E；
- [x] Host RawSignal→ONNX→SQL→EventBus 生命周期；
- [x] 正常不 Raise；
- [x] 异常与恢复精确；
- [x] 活动异常切版阻断；
- [x] 恢复后切版与重启恢复；
- [x] 坏 Hash Host 健康且模型拒载；
- [x] 20,000 次推理、吞吐和 256 MB RSS 门槛；
- [x] 旧 Isolation Forest、Governance、Context、Version、Health 和 Maintenance 无回归；
- [x] One Hour Soak；
- [x] Production 默认关闭；
- [x] 首轮 exact head 20/20；
- [ ] 最终证据 Head 再次 20/20；
- [ ] PR Squash 合入 develop。

## 8. 现场验收

仓库级验收不代表现场验收。现场仍须签署训练数据来源和授权、真实故障样本、准确率/召回率/误报率/校准、工况分层、数据泄漏检查、CPU/内存/磁盘容量、模型许可证与安全扫描、权限、展示契约、Shadow/Canary 结果、回退演练及不接入自动控制的安全确认。

## 9. 当前记录

| 项目 | 结果 |
|---|---|
| 功能代码 | 已实现 |
| 默认关闭 | 已实现 |
| Adapter Compile | #28 成功 |
| Real ONNX E2E | #21 成功 |
| Host 生命周期 E2E | #11 成功 |
| 首轮完整回归 | 20/20 成功 |
| 最终证据复验 | 待新 Head 完成 |
| 合并 | 尚未执行 |
| 现场投产 | 未验收 |

本文件提交产生的新 exact head 必须再次通过同等完整矩阵，旧 Head 的成功不得替代。