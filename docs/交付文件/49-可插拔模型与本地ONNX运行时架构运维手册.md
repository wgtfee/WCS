# 可插拔模型与本地 ONNX 运行时架构运维手册

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 能力版本 | AnomalyEngine v3.8 |
| 代码分支 | `feature/pluggable-onnx-runtime-v3-8` |
| 运行方式 | .NET 8、Microsoft ONNX Runtime CPU、本地文件、离线推理 |
| 安全定位 | 诊断 Evidence 与异常生命周期，不直接控制设备 |
| 默认状态 | `MachineLearning.Enabled=false`、`PluggableRuntime.Enabled=false` |

## 2. 目标与边界

v3.8 在保留现有纯 .NET Isolation Forest 训练、Shadow、Canary、审批、回滚和生命周期能力的基础上，增加统一模型适配边界，使经过离线训练和人工审批的本地 ONNX 模型能够复用 WCS 已有特征窗口、候选 Journal、EventBus 和异常恢复链路。

明确不包含：

- 在线下载模型；
- 云端推理；
- 自动训练、自动审批或自动激活外部模型；
- GPU、CUDA 或 DirectML 依赖；
- PLC 写入、设备停机、任务取消、路线、路权、车辆选择或派单修改；
- 将模型分数当作安全联锁或实际故障概率。

## 3. 架构

```text
RawSignalEvent
      ↓
PlcAnomalySample
      ↓
IPlcMlAnomalyEngine
      ↓
PluggablePlcMlAnomalyEngine
      ├─ 非外部 Profile → 原 PlcMlAnomalyEngine / Isolation Forest
      └─ 显式外部 Profile → PlcFeatureWindowEngine
                              ↓
                    PlcMlModelAdapterRegistry
                      ├─ IsolationForest Adapter
                      └─ ONNX Runtime CPU Adapter
                              ↓
                     Candidate SQL + EventBus
                              ↓
                   Fusion / Health / Governance
```

DI 会为旧引擎创建一份排除外部 Profile 的配置副本。外部 Profile 由 Decorator 单独建立特征窗口，因此同一 Profile 不会同时进入旧模型和 ONNX 模型。

## 4. 核心契约

### 4.1 Manifest

`PlcMlModelManifest` 包含：

- SchemaVersion；
- ProfileId、Version；
- AdapterKind、AdapterId；
- 本地 ArtifactFile；
- ArtifactSha256；
- CreatedUtc、Source、ApprovedBy、ApprovedAtUtc；
- FeatureNames、Means、StandardDeviations；
- InputName、OutputName、InputShape；
- ScoreTransform、DecisionThreshold；
- CalibrationMeanScore、CalibrationP95Score。

Manifest 不包含下载 URL、密钥或远程端点。

### 4.2 适配器

- `IPlcMlModelAdapter`：负责验证并加载模型；
- `IPlcMlModelRuntime`：持有可复用推理会话并执行 Predict；
- `PlcMlModelAdapterRegistry`：按 AdapterKind 唯一注册；
- `IPlcMlExternalModelStore`：加载活动版本、列版本、激活版本；
- `IPlcMlExternalRuntimeStatusProvider`：提供只读状态。

## 5. 本地模型目录

```text
<ModelDirectory>/external/<ProfileId>/
├── active.json
├── manifest-<Version>.json
└── <ArtifactFile>
```

约束：

1. `ArtifactFile` 必须是相对文件名；
2. 禁止绝对路径和 `..` 目录穿越；
3. 单文件最大 256 MB；
4. 加载前验证 SHA-256；
5. `active.json` 使用原子临时文件替换；
6. 同一 Profile 的 Manifest 和模型文件必须位于其专用目录；
7. 生产目录必须使用受控 ACL、备份和发布签名流程。

## 6. 特征契约

`PlcMlFeatureSchema` 从 Profile 信号定义生成确定性特征顺序。

Numeric 信号：

```text
mean
stddev
min
max
last
slope
range
samplesPerSecond
```

Boolean 信号：

```text
trueRatio
transitions
last
samplesPerSecond
```

Manifest 的 FeatureNames 必须与 Profile 生成结果逐项、区分大小写、顺序一致。Means、StandardDeviations 和 ONNX 输入第二维必须与特征数量一致。特征定义变化必须产生新的模型版本和 Manifest。

## 7. ONNX Runtime Adapter

v3.8 使用 CPU 包，并固定：

- `ORT_SEQUENTIAL`；
- InterOp 与 IntraOp 各 1 线程；
- 全图优化；
- CPU Arena 和 Memory Pattern；
- float32 输入和输出；
- `[1|-1, FeatureCount]` 二维输入；
- 一个已命名分数输出。

`InferenceSession` 在模型加载时创建并复用，版本切换或 Host 关闭时显式 Dispose。每次推理创建并释放结果集合，不跨请求保存 Tensor。

## 8. 分数与解释

支持：

- Identity；
- Sigmoid；
- OneMinus。

变换后分数必须在 `[0,1]`。外部模型正式阈值取：

```text
max(Manifest.DecisionThreshold, Profile.ObserveThreshold, Profile.WarningThreshold)
```

异常解释包含模型分数、阈值和绝对标准化偏差最大的三个特征。该分数用于诊断排序和生命周期，不是实际故障概率。

## 9. 生命周期与治理

外部 Profile 复用：

- ConsecutiveAbnormalCount；
- ConsecutiveRecoveryCount；
- Shadow、Canary、Active；
- Canary 按 ProfileId + DeviceId 确定性分桶；
- `Wcs_PlcMlCandidate` 候选 Journal；
- `PlcAnomalyDetectedEvent` / `PlcAnomalyRecoveredEvent`；
- Fusion、健康评分和后续治理链路。

活动异常存在时禁止切换外部模型版本。恢复后可通过现有模型激活 API 切换，Store 原子更新 `active.json`，旧 Runtime 在替换后释放。

## 10. 失败隔离

以下错误只记录到外部运行时状态，不阻断 Host：

- 活动 Manifest 缺失；
- SHA-256 不匹配；
- Profile / FeatureSchema 不匹配；
- Adapter 未注册；
- ONNX 输入输出名称、类型或维度不匹配；
- native runtime 加载失败；
- 单次推理异常。

`Required=true` 表示缺少模型必须呈现为失败状态，不表示 Host 启动失败。无有效 Runtime 时不产生预测或控制动作。

## 11. API

现有 API 继续适用：

```text
GET  /api/anomaly/ml/status
GET  /api/anomaly/ml/models/{profileId}
POST /api/anomaly/ml/models/{profileId}/{version}/activate
POST /api/anomaly/ml/train/{profileId}
```

外部 Profile 调用训练 API会被拒绝，因为只允许离线导入已审批 Artifact。

新增只读接口：

```text
GET /api/anomaly/ml/adapters/status
```

返回 Adapter、活动版本、ManifestHash、ArtifactSha256、预测、Raise、Recover、失败和窗口指标。

## 12. 配置

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": false,
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

现场启用时，一个外部 Profile 必须同时满足：

- 在 `MachineLearning.Profiles` 中存在且 Enabled；
- `CollectTrainingData=false`；
- `AutoTrain=false`；
- 在 `PluggableRuntime.Profiles` 中唯一声明；
- 本地活动 Manifest 完整、已审批且 Hash 正确；
- Profile FeatureSchema 与 Manifest 完全一致；
- DeploymentMode 按 Shadow → Canary → Active 推进。

## 13. 发布流程

1. 离线冻结数据集和特征版本；
2. 训练并导出 ONNX；
3. 独立验证输入输出、精度、吞吐、内存和许可证；
4. 生成 SHA-256 和 Manifest；
5. 人工审批 Source、ApprovedBy、ApprovedAtUtc；
6. 将模型和版本 Manifest 发布到 Profile 目录；
7. 先 Shadow；
8. 审查 Candidate、误报和漂移；
9. Canary；
10. 无活动异常时激活目标版本；
11. 观察状态、日志、SQL 与 Fusion；
12. 必要时回滚上一活动版本。

## 14. 回退

最快回退：

```text
AnomalyDetection__MachineLearning__PluggableRuntime__Enabled=false
```

也可将单个 Profile 从 `PluggableRuntime.Profiles` 移除，恢复原 Isolation Forest 路径；前提是旧模型版本、训练数据和配置仍完整。回退不删除候选 Journal、模型文件或 Manifest。

## 15. 运维检查

- [ ] 仓库和 Production 默认关闭；
- [ ] 没有 URL、密钥或真实模型提交到仓库；
- [ ] Artifact SHA-256 与 Manifest 一致；
- [ ] Profile 和特征顺序一致；
- [ ] ONNX float32 输入输出及维度通过；
- [ ] Shadow/Canary/Active 和回滚经过审批；
- [ ] 活动异常期间切版被阻断；
- [ ] Host 重启恢复活动版本；
- [ ] 坏 Hash 时 Host 健康但模型拒载；
- [ ] SQL、EventBus、Fusion 与恢复记录可追溯；
- [ ] 无 PLC 写入、停机、任务取消或调度修改。
