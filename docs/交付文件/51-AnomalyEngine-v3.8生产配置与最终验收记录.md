# AnomalyEngine v3.8 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | v3.8 |
| 能力 | 可插拔模型与本地 ONNX Runtime |
| 默认状态 | 关闭 |
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

仓库不包含：

- 真实 ONNX 模型；
- 现场 Manifest；
- 下载 URL；
- 密钥或许可证文件；
- 真实 PLC 点位和资产编号；
- 自动激活配置。

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

不得直接从关闭状态跳到 Active。

## 4. Profile 配置约束

外部 Profile：

- 必须在 `MachineLearning.Profiles` 中唯一；
- `Enabled=true`；
- `CollectTrainingData=false`；
- `AutoTrain=false`；
- Signals 和顺序与离线训练一致；
- `DeploymentMode` 初始为 Shadow；
- 在 `PluggableRuntime.Profiles` 中声明相同 ProfileId；
- AdapterKind 与 Manifest 相同；
- `Required=true` 时缺少活动模型必须报警并保持无推理。

示例结构仅说明字段，不代表生产值：

```json
{
  "AnomalyDetection": {
    "MachineLearning": {
      "Enabled": true,
      "PluggableRuntime": {
        "Enabled": true,
        "MaximumTrackedWindows": 5000,
        "InactiveStateRetentionSeconds": 300,
        "Profiles": [
          {
            "ProfileId": "<approved-profile>",
            "AdapterKind": "Onnx",
            "Required": true
          }
        ]
      },
      "Profiles": [
        {
          "ProfileId": "<approved-profile>",
          "Enabled": true,
          "CollectTrainingData": false,
          "AutoTrain": false,
          "DeploymentMode": "Shadow",
          "Signals": []
        }
      ]
    }
  }
}
```

## 5. Manifest 签署项

每个版本必须记录：

- ProfileId；
- Version；
- AdapterKind / AdapterId；
- ArtifactFile；
- ArtifactSha256；
- CreatedUtc；
- Source；
- ApprovedBy；
- ApprovedAtUtc；
- FeatureNames；
- Means / StandardDeviations；
- InputName / OutputName / InputShape；
- ScoreTransform；
- DecisionThreshold；
- Calibration 指标；
- 训练数据版本和离线测试报告外部编号；
- 模型许可证和第三方组件清单外部编号。

同一 Version 不得对应不同模型内容或特征契约。

## 6. 目录与权限

- 模型目录不得位于临时目录；
- WCS 运行账号只需读取模型、读取版本 Manifest、原子更新 `active.json`；
- 普通操作员不得修改模型文件；
- 发布账号与运行账号分离；
- 目录纳入备份、校验和防病毒排除策略；
- 禁止通过共享匿名目录或公网 URL 加载模型；
- 现场变更必须保存发布单和回退版本。

## 7. 运行监控

接口：

```text
GET /api/anomaly/ml/adapters/status
```

至少监控：

- ActiveAdapterId；
- ActiveModelVersion；
- ManifestHash；
- ArtifactSha256；
- Predictions；
- AnomalyObservations；
- Raised / Recovered；
- ShadowRaised / ActiveRaised；
- Failures；
- ActiveAnomalies；
- TrackedInferenceStates；
- CompletedWindows / DroppedIncompleteWindows；
- LastError。

必须与 SQL Candidate、Fusion Evidence、健康事件和维护反馈联查。

## 8. 切版与回滚

切版前：

- 新版本文件和 Manifest 已发布；
- Hash 和 FeatureSchema 已验证；
- 无活动异常；
- Candidate 已审查；
- 回退版本存在；
- 操作人身份和变更单有效。

活动异常存在时 API 必须拒绝切版。切版成功后更新 `active.json`，Host 重启仍应恢复相同版本。

紧急关闭：

```text
AnomalyDetection__MachineLearning__PluggableRuntime__Enabled=false
```

关闭只停止外部模型推理，不删除模型、Candidate 或审计数据，不影响 PLC、任务、路权和调度。

## 9. 故障处理

### 9.1 Hash mismatch

表现：Host 健康，外部状态无活动版本，Failures 增加，LastError 包含 Hash 错误。

处理：隔离文件 → 对照发布 Hash → 恢复正确模型和 Manifest → 重启或重新激活 → 核验状态。

### 9.2 FeatureSchema mismatch

表现：模型拒载，不产生预测。

处理：禁止临时调整特征顺序绕过；重新导出匹配 Profile 的模型，或升级 Profile 与模型版本并重新审批。

### 9.3 Native runtime 缺失

表现：ONNX Session 创建失败。

处理：核对发布目录的 `Microsoft.ML.OnnxRuntime.dll` 和平台对应 `libonnxruntime`，检查依赖和架构，不允许切换远程推理绕过。

### 9.4 推理失败或资源异常

表现：Failures 增加，旧控制链路继续运行。

处理：切回 Shadow 或关闭 PluggableRuntime，保留证据，检查模型维度、输出、内存和并发；不得扩大线程或内存上限掩盖缺陷。

## 10. 仓库级验收

最终应满足：

- [ ] Core Manifest / FeatureSchema 测试通过；
- [ ] Adapter Compile 干净构建通过；
- [ ] 真实 ONNX native E2E 通过；
- [ ] Host RawSignal→ONNX→SQL→EventBus 生命周期通过；
- [ ] 正常不 Raise；
- [ ] 异常 Raise 精确；
- [ ] 恢复精确；
- [ ] 活动异常期间切版阻断；
- [ ] 恢复后切版成功；
- [ ] 重启恢复活动版本；
- [ ] 坏 Hash Host 健康且模型拒载；
- [ ] 20,000 次推理吞吐和 256 MB RSS 门槛通过；
- [ ] 旧 Isolation Forest、Governance、Context、Version、Health 和 Maintenance 无回归；
- [ ] One Hour Soak 通过；
- [ ] Production 默认关闭；
- [ ] 最终证据提交后 exact-head 再次全绿；
- [ ] PR Squash 合入 develop。

## 11. 现场验收

仓库级验收不代表现场验收。现场还必须签署：

- 模型训练数据来源和授权；
- 真实缺陷/故障样本；
- 离线准确率、召回率、误报率和校准；
- 工况分层和数据泄漏检查；
- 现场 CPU、内存和磁盘容量；
- 模型许可证与安全扫描；
- 操作权限和审批；
- MES、维修和告警展示契约；
- Shadow / Canary 观察结果；
- 回退演练；
- 不接入自动控制的安全确认。

## 12. 最终记录

| 项目 | 结果 |
|---|---|
| 功能代码 | 已实现，等待最终矩阵 |
| 默认关闭 | 已实现 |
| Adapter Compile | 开发期已成功 |
| Real ONNX E2E | 开发期已成功 |
| Host 生命周期 E2E | 主要链路已成功，SQL 测试脚本已修正，等待最终成功运行 |
| 完整回归 | 等待 latest exact head |
| 合并 | 尚未执行 |
| 现场投产 | 未验收 |

最终运行号、Artifact、Digest、Head 和 merge SHA 在最终收口时回填。
