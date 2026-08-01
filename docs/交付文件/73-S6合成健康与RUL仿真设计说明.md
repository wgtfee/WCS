# S6 合成健康与 RUL 仿真设计说明

## 1. 目标与边界

S6 在既有 Simulation & Verification S0～S5 基础上增加 `VirtualHealth`，用于构造确定性的健康退化、维修恢复、故障终点和删失样本，并验证既有 AnomalyEngine v3.4 健康历史与 v3.9 Forecast/RUL 契约。

S6 的 Forecast Oracle 只是 Simulation expected output。它不是 v3.9 的真实 `AssetFailureForecastPrediction`，不加载批准 ONNX 模型，不调用 `IAssetFailureForecastService`，不写生产 Forecast SQL，不向 PLC、任务、设备、路线、预约或调度控制路径输出动作。

## 2. 核心实现

- `VirtualHealthRuntime`：全部状态写入 S1 `SimulationStateStore`，因此 Checkpoint、Replay 与 FinalStateHash 覆盖 S6 状态。
- `VirtualHealthOptions`：限制资产、历史点、Forecast Oracle、Outcome 与 Audit 数量。
- `VirtualHealthScenarioHandlers`：提供确定性 DSL action/assertion。
- `SimulationVirtualHealthController`：仅提供 Simulation/SimulationLoadTest 下的只读检查。
- `SimulationHostRuntime`：与 S2～S5 共用一个 Composition Root。

## 3. 与 v3.4/v3.9 的契约对齐

S6 不复制 Forecast 特征定义，而是直接调用现有 `AssetFailureForecastFeatureBuilder` 构造受治理的 14 维特征：

`health.latest`、`health.mean`、`health.minimum`、`health.maximum`、`health.stddev`、`health.slopePerHour`、`health.delta`、`fusionRisk.mean`、`fusionRisk.maximum`、`grade.changeCount`、`grade.degradedOrWorseRatio`、`grade.criticalRatio`、`history.sampleCount`、`history.spanHours`。

Forecast Oracle 输出必须通过现有 `AssetFailureForecastManifestValidator.ValidateOutput`：

- `0 <= P24 <= P72 <= P168 <= 1`；
- `0 <= RulLower <= RulMedian <= RulUpper <= MaximumRulHours`；
- 所有数值必须 finite。

## 4. 确定性健康场景

S6 支持：

1. 定义合成资产；
2. 记录单点健康样本；
3. 使用虚拟时间生成有界线性退化曲线；
4. 维修后恢复健康分；
5. 写入 Forecast Oracle；
6. 写入 ObservedFailure、PreventiveMaintenance、CensoredNoFailure 等 Outcome；
7. 比较概率趋势与 RUL 趋势；
8. Checkpoint 恢复后继续执行并验证最终 State Hash。

退化场景要求概率不下降、RUL Median 不上升；维修恢复场景允许建立新的后维修基线，但不会自动修改任何生产维修、设备或控制状态。

## 5. DSL

Actions：

- `health.asset.define`
- `health.sample.record`
- `health.profile.linear`
- `health.maintenance.restore`
- `health.forecast.oracle`
- `health.outcome.record`

Assertions 覆盖健康等级、健康分、样本数、趋势、14 维特征合法性、Forecast 契约、RUL 趋势、概率趋势和 Outcome。

## 6. 安全与隔离

`src/Wcs.Simulator/VirtualHealth` 禁止依赖：真实 ONNX Runtime、SQL/SqlSugar、HTTP/Socket、生产 Forecast Service、PLC Client、CommandBus、TaskScheduler、TaskOrchestrator、DeviceManager、Dispatch/Traffic Reservation。

同时禁止 `DateTime.Now`、`DateTime.UtcNow`、`Task.Delay`、`Thread.Sleep`、`Random.Shared` 等墙钟或非确定性来源。

Simulation 配置中的 `AssetFailureForecast.Enabled=false` 保持不变。

## 7. Functional Head 首轮证据

Functional Head：`8bf4adc941613b304fc3d7a3defbae3333b761a7`

- Synthetic Health RUL #2 / Run `30707161720`：11/11 success；Artifact `wcs-simulation-synthetic-health-rul-2`，ID `8820689033`，Digest `sha256:eb12c05af7855fdd9bc31902cca7d9bf3bd7f96a6020fd17d54b29d402fdfd0d`。
- Health RUL Determinism #2 / Run `30707161766`：8/8 success；Artifact `wcs-simulation-health-rul-determinism-2`，ID `8820689088`，Digest `sha256:acc6fd9798f8baa494ddeda1af7b641055f15daedd738082efdd96b7e89dd65f`。
- S6 Full Regression #1 / Run `30707161825`：39/39 exact-head success；Artifact `wcs-simulation-s6-full-regression-1`，ID `8821390450`，Digest `sha256:d0aa64d751ea79002a6f1e52d8fad17d8bbce29213f33855879c71d01dac036e`。

Full Regression 已核实 `workflowCount=39`、`allSuccess=true`、39 个 child 均 `completed/success` 且 `headSha` 等于 Functional Head，包含 `WCS One Hour Soak Load`，PR Head `expected == actual`。

## 8. 最终验收规则

本设计说明与 74、75、00、21 文档提交形成新的 Evidence Head。最终 Evidence Head 必须重新通过同样的 11/11、8/8、39/39 exact-head 门禁；第二轮证据仅写入 PR Conversation，不再修改仓库文件。