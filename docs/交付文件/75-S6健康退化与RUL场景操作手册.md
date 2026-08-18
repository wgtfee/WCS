# S6 健康退化与 RUL 场景操作手册

## 1. 用途

本手册用于在 `Simulation` / `SimulationLoadTest` 环境中开发和检查 S6 合成健康退化、Forecast Oracle、RUL 趋势和 Outcome 场景。

Forecast Oracle 是 Simulation expected output，不是真实 v3.9 Forecast。禁止把 Oracle 数值解释为现场设备真实故障概率、厂家寿命或自动维修/停机依据。

## 2. 前置条件

- `Simulator.Enabled=true`
- `SimulationGovernance.Enabled=true`
- Environment 属于批准的 Simulation allow-list
- `AssetFailureForecast.Enabled=false` 可继续保持关闭；S6 不需要真实模型
- 场景必须遵守 S0 Manifest、Version、Seed、Hash 与审批规则

## 3. 常用动作

### 3.1 定义资产

`health.asset.define` 创建 Simulation 内的资产状态。资产 ID 必须稳定且仅用于仿真。

### 3.2 记录样本

`health.sample.record` 写入确定性 HealthScore/FusionRisk/Grade 样本。样本进入 `SimulationStateStore`，不会写生产健康历史 SQL。

### 3.3 生成退化曲线

`health.profile.linear` 使用虚拟时间生成有界线性退化。用于快速构造 24h、48h、72h 等历史窗口，满足 v3.9 FeatureBuilder 的点数与跨度要求。

### 3.4 维修恢复

`health.maintenance.restore` 在 Simulation State 中建立维修后恢复点，用于验证健康恢复与新基线场景；不调用真实 Maintenance Service。

### 3.5 写入 Forecast Oracle

`health.forecast.oracle` 保存期望的 P24/P72/P168 与 RUL Lower/Median/Upper。写入前必须通过现有 v3.9 `ValidateOutput`。

### 3.6 记录 Outcome

`health.outcome.record` 可表达 ObservedFailure、PreventiveMaintenance、CensoredNoFailure 等模拟结果，仅用于 Scenario evidence。

## 4. 常用断言

S6 断言覆盖：

- 当前 Health Grade；
- 当前 Health Score；
- 历史样本数量；
- Deteriorating/Improving/Stable 趋势；
- 14 维 Forecast Feature Vector 是否可构建；
- Forecast Oracle 是否满足 v3.9 输出契约；
- 多次 Oracle 的概率是否单调不下降；
- 多次 Oracle 的 RUL Median 是否单调不升；
- Outcome 是否存在并匹配预期。

建议对关键场景同时断言 Feature 有效、概率趋势、RUL 趋势和最终 Outcome，而不是只检查一个最终数值。

## 5. 推荐场景模板

### 5.1 渐进退化

1. 定义资产；
2. 用 `health.profile.linear` 生成至少 48 个点和至少 24 小时历史；
3. 断言 Feature Vector 可构建；
4. 在早、中、晚三个阶段记录 Oracle；
5. 断言 P24/P72/P168 合法且整体风险上升；
6. 断言 RUL Median 下降；
7. Checkpoint 后继续执行并 Replay。

### 5.2 维修恢复

1. 先执行退化场景；
2. 写 PreventiveMaintenance Outcome；
3. `health.maintenance.restore` 提升健康分；
4. 继续记录新样本；
5. 验证恢复后趋势与新 Oracle，不修改原退化证据。

### 5.3 故障终点

1. 逐步退化至 Critical；
2. 记录高风险、低 RUL Oracle；
3. 写 ObservedFailure Outcome；
4. Replay 两次并比较最终 State/Evidence Hash。

### 5.4 删失样本

1. 创建长期历史但不触发故障；
2. 在观察期末写 CensoredNoFailure；
3. 确认原 Forecast Oracle 和 Outcome 都保留在 Simulation State。

## 6. 只读 Host 检查

`SimulationVirtualHealthController` 可查看 run 的 status、assets、samples、feature vector、trend、forecast oracle、outcomes 和 audit。控制器通过当前 Run Checkpoint 重建只读 Runtime，不提供绕过 DSL 的写入接口。

Production、未知 Run 或未批准环境返回 404。

## 7. Checkpoint / Replay

S6 所有持久仿真状态都位于共享 `SimulationStateStore`：资产索引、样本、Oracle、Outcome、Audit 和序列号。因此：

- Checkpoint 包含 S6 状态；
- restore 后应得到相同 canonical JSON；
- 同场景同 Seed 的 Replay 应得到相同 StateHash/EvidenceHash；
- 不允许使用墙钟或随机来源修正结果。

## 8. 排错

- Feature 无效：检查历史点数、时间跨度、HealthScore 0～100、FusionRisk 0～1 以及时间顺序。
- Forecast Oracle 被拒绝：检查 `P24 <= P72 <= P168`、RUL `Lower <= Median <= Upper`、上界和 finite。
- 趋势断言失败：确认 Oracle 顺序与退化方向是否一致。
- Replay Hash 不同：检查是否新增墙钟、sleep、Random 或非 StateStore 状态。
- Host 404：检查环境名、Simulator、SimulationGovernance allow-list 和 runId。

## 9. CI 验收

最终 Evidence Head 必须同时通过：

- WCS Simulation Synthetic Health RUL：11/11；
- WCS Simulation Health RUL Determinism：8/8；
- WCS Simulation S6 Full Regression：39/39，`workflowCount=39`，`allSuccess=true`，所有 child `headSha` 等于 Evidence Head，PR Head 不漂移。

第二轮完成后仅把 Run、Artifact ID/Name/Digest 和 exact-head evidence 写入 PR #39 Conversation，不再提交文件，然后才允许 Ready + Squash Merge。