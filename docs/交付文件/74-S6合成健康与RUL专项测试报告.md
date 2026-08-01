# S6 合成健康与 RUL 专项测试报告

## 1. 测试范围

本报告覆盖 Simulation & Verification S6 的 Synthetic Health / RUL 仿真验证。测试对象仅为 Simulation 层，不代表真实模型精度、现场剩余寿命、设备安全寿命或维修指令。

## 2. Functional Head

`8bf4adc941613b304fc3d7a3defbae3333b761a7`

## 3. 专项门禁

### 3.1 WCS Simulation Synthetic Health RUL

- Run：`30707161720`
- Run number：#2
- 结果：success
- 测试：11/11
- Artifact：`wcs-simulation-synthetic-health-rul-2`
- Artifact ID：`8820689033`
- Expired：false
- SHA-256 Digest：`sha256:eb12c05af7855fdd9bc31902cca7d9bf3bd7f96a6020fd17d54b29d402fdfd0d`

门禁覆盖 Runtime、Host composition、Production 404、unknown-run 404、只读 inspection、v3.9 FeatureBuilder/ValidateOutput 复用、容量配置与 Simulation-only 静态隔离。

### 3.2 WCS Simulation Health RUL Determinism

- Run：`30707161766`
- Run number：#2
- 结果：success
- 测试：8/8
- Artifact：`wcs-simulation-health-rul-determinism-2`
- Artifact ID：`8820689088`
- Expired：false
- SHA-256 Digest：`sha256:acc6fd9798f8baa494ddeda1af7b641055f15daedd738082efdd96b7e89dd65f`

门禁验证退化曲线、Feature Vector、Forecast Oracle、Outcome、Checkpoint/Replay 与 FinalStateHash 的确定性，并静态拒绝墙钟、sleep、随机、SQL、网络、真实 ONNX/Forecast Service 与生产控制依赖。

## 4. 39-child Full Regression

- Workflow：WCS Simulation S6 Full Regression
- Run：`30707161825`
- Run number：#1
- 结果：success
- Artifact：`wcs-simulation-s6-full-regression-1`
- Artifact ID：`8821390450`
- Expired：false
- SHA-256 Digest：`sha256:d0aa64d751ea79002a6f1e52d8fad17d8bbce29213f33855879c71d01dac036e`

Evidence：

```text
expectedHeadSha=8bf4adc941613b304fc3d7a3defbae3333b761a7
workflowCount=39
allSuccess=true
39/39 child status=completed
39/39 child conclusion=success
39/39 child headSha=8bf4adc941613b304fc3d7a3defbae3333b761a7
PR Head expected == actual
```

39 条矩阵包含历史 25 条 Anomaly/Forecast/Load/Soak 基线、S0～S5 两条阶段专项以及 S6 两条专项。其中 `WCS One Hour Soak Load` 已在 exact Functional Head 上成功。

## 5. 关键验收点

1. 合成健康历史能够满足 v3.9 `MinimumHistoryPoints`/`MinimumHistorySpanHours` 所需的受治理输入结构。
2. 14 维 FeatureSchema 直接复用生产定义，没有在 Simulation 内另建漂移 schema。
3. Forecast Oracle 必须满足 P24/P72/P168 单调概率约束。
4. Forecast Oracle 必须满足 RUL Lower/Median/Upper 有序与上界约束。
5. 退化趋势验证概率不下降、RUL Median 不上升。
6. 维修恢复、ObservedFailure、CensoredNoFailure 等结果只写 Simulation State。
7. Checkpoint restore 与 Replay 的 State/Evidence Hash 保持确定性。
8. Host 仅开放只读检查；Production 和未批准环境返回 404。
9. `AssetFailureForecast.Enabled=false` 未被 S6 修改。
10. 无真实模型、SQL、网络或控制路径依赖。

## 6. 风险声明

S6 Oracle 不等于真实 v3.9 Forecast。它只是用于判断“如果给定受治理的期望概率/RUL，整个仿真、契约、回放和趋势验证链是否正确”。真实故障概率和 RUL 的有效性仍依赖批准训练数据、真实失败/删失样本、批准 ONNX 模型、独立验证指标和现场验收。

## 7. 第二轮要求

73～75、00、21 文档提交后形成 Evidence Head。该 Head 必须重新执行 Synthetic Health RUL 11/11、Health RUL Determinism 8/8、Full Regression 39/39；第二轮 Artifact/Digest 与 exact-head 结果只写入 PR #39 Conversation，避免再产生新的 Head。