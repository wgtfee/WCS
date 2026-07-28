# AnomalyEngine v3.5～v3.9 总体演进路线

## 1. 文档定位

本文件定义 WCS Runtime Engine 在 v3.4 之后的独立异常诊断演进路线。

```text
EMS/RGV 统一调度主线：第一阶段～第十二阶段，软件研发已经结束
AnomalyEngine 诊断扩展线：v1～v3.9，继续以只读诊断能力演进
```

统一调度后续不再使用“第十三阶段”扩展。现场工作使用点位包、拓扑包、参数包、缺陷单、验收报告和发布单推进。AnomalyEngine 不得绕过既有状态机、路权、PLC 联锁、AlarmCenter 和审批体系。

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
| v3.6 | 根因关联、传播路径、SQL 快照和人工复核 | PR #29 最终仓库级验收中 |

## 3. 总体数据流

```text
规则 / 统计 / ML / 同群 / 周期异常
                 ↓
v3.3 Evidence Fusion
                 ↓
v3.4 Health Score + Trend
                 ↓
v3.5 Governed Health Event + MES Outbox
                 ↓
v3.6 Root Cause Graph Analysis + Review Journal
                 ↓
v3.7 Maintenance Decision and Feedback Loop
                 ↓
v3.8 Pluggable Model / ONNX Runtime
                 ↓
v3.9 Failure Probability and RUL
```

v3.5～v3.9 均属于诊断与辅助决策层。自动停机、自动修改调度、自动释放路权或自动下发维修动作必须进入独立安全项目。

## 4. v3.5：健康事件治理与 MES 联动

### 4.1 已交付能力

- 按最低健康等级和连续评估次数创建事件；
- 连续恢复确认，避免抖动；
- 同一资产一个活动健康事件；
- Raised、Observed、GradeChanged、Acknowledged、Suppressed、Unsuppressed、Recovered；
- SQL Journal 使用 MessageId 和 EventId + Version 幂等；
- MES HTTP Outbox 支持超时、指数退避、DeadLetter 和人工重试；
- 2xx/409 按幂等成功处理；
- Host 重启恢复活动事件和待发送状态；
- SQL、MES 或网络故障不阻塞 PLC、任务和调度。

### 4.2 安全边界

- 不停止设备；
- 不取消任务；
- 不改变车辆选择、路径或路权；
- 不替代 AlarmCenter；
- MES 响应不是 PLC 联锁依据；
- 仓库不保存 MES 密钥。

## 5. v3.6：根因关联与异常传播分析

### 5.1 目标

将同一时间窗口内的设备、部件、信号、任务、站点和路段健康事件建立确定性依赖关系，输出根因候选和传播路径，避免向人员展示大量孤立问题。

### 5.2 已实现能力

- 版本化、带来源和审批信息的依赖图；
- Asset、Component、Signal、Task、Station、Segment 节点；
- DependsOn、Feeds、Controls、LocatedAt、Carries 有向关系；
- GraphHash 和同版本不同 Hash 拒绝；
- 节点、边、引用、权重、自环、容量和环路校验；
- 生产默认 `AllowCycles=false`；
- 按活动 v3.5 健康事件和有界时间窗口关联；
- 有界上游搜索和最短传播路径；
- RootCause、Intermediate、Symptom 角色；
- Coverage、Topology、Temporal、Severity 可解释排序；
- 确定性 AnalysisId 和 SQL 幂等；
- SQL 图版本、不可变分析快照、人工复核 Journal；
- Confirmed、Rejected、Supplemented；
- Host 重启后分析和复核可查询；
- SQL 或分析失败不影响控制链路。

### 5.3 明确边界

- Confidence 是诊断排序，不是实际故障概率；
- 无图映射时不猜测节点；
- Supplemented 不自动修改活动图；
- 图修改必须升级 Version 并重新审批；
- 根因结果不自动停机、不取消任务、不修改路线和调度；
- 当前不是贝叶斯因果模型，也不使用生成式 AI 自动生成现场拓扑。

### 5.4 完成门槛

- Core 图校验、排名、深度、时间窗口和幂等测试通过；
- SQL 图版本、Analysis、Review 精确计数通过；
- 传播链角色和深度断言通过；
- Confirmed 和 Supplemented Journal 可追溯；
- Host 重启恢复通过；
- 最新源码和文档提交的完整 CI、负载和一小时 Soak 全绿；
- 默认 `Enabled=false`，Production 图为空；
- 文档 21、39、43～45 完整。

## 6. v3.7：维修决策支持与反馈闭环

### 6.1 目标

把 v3.5 健康事件和 v3.6 已复核根因转换为可执行检查建议，并利用 MES/维修反馈评估误报、命中率和维修效果。

### 6.2 计划能力

- 根因到检查项、部件、工具、备件和安全注意事项的规则映射；
- 生成建议检查，不生成控制命令；
- Accepted、Rejected、FalsePositive、Repaired、NoFaultFound 等反馈；
- 保存维修前后健康分、根因和事件变化；
- 统计采纳率、命中率、误报率和平均关闭时间；
- 人工确认结果形成候选训练标签，但不自动污染活动模型；
- 关联 MES 工单、处理人和完成时间；
- 配置版本、审批和回退。

### 6.3 前置条件和门槛

- v3.5 事件治理稳定；
- v3.6 结果达到可解释和人工复核门槛；
- 现场提供标准维修作业和部件字典；
- 维修反馈不得覆盖原始诊断证据；
- 无现场规则时不生成伪建议；
- 不自动申请停机或绕过生产审批。

## 7. v3.8：可插拔模型与 ONNX 运行时

### 7.1 目标

保留纯 .NET Isolation Forest，同时建立统一模型适配边界，使本地 ONNX 模型复用 Profile、治理、Shadow、Canary、回滚和 Evidence Fusion。

### 7.2 计划能力

- `IAnomalyModelAdapter`；
- 统一特征、输入维度、标准化和输出解释契约；
- Isolation Forest Adapter 和本地 ONNX Runtime Adapter；
- 模型摘要、输入输出名称和维度校验；
- 冻结数据集、审批、Shadow、Canary 和原子激活；
- 模型输出只生成 Evidence，不写 PLC；
- 推理超时、异常和内存有界；
- 无 GPU、无外网环境可运行。

### 7.3 启动条件

仅在存在经过现场验收的本地模型、稳定特征和明确许可证时实施。需通过重复性、十万窗口吞吐、回滚、内存和失败降级测试。

## 8. v3.9：故障概率与剩余寿命预测

### 8.1 目标

在具备长期退化数据、真实故障和维修记录后，输出部件未来故障概率、剩余寿命区间和建议维护窗口。

### 8.2 计划能力

- 部件级退化轨迹；
- 指定时间窗内故障概率；
- RUL 点估计和置信区间；
- 按产品、负载、工况和维修状态分层；
- 时间切分、数据泄漏检查和离线回测；
- 校准、覆盖率、提前量和误报评估；
- 维修后退化状态重置或迁移；
- 输出建议维护窗口，不自动下发停机。

### 8.3 数据门槛

必须有足够长的连续历史、明确失效时间、部件更换与维修记录、工况上下文、可信标签和足够真实失效样本。数据不足时保持关闭，不输出虚假 RUL。

## 9. 执行顺序

```text
v3.5 已完成
  ↓
v3.6 当前收口
  ↓
v3.7 建议下一步实施
  ↓
v3.8 仅在成熟本地模型存在时实施
  ↓
v3.9 仅在真实失效和维修数据满足条件时实施
```

## 10. 统一安全原则

所有阶段必须满足：

1. 默认关闭；
2. 输入、容量、时间窗口、队列、查询和保留期有界；
3. 结果可解释、可版本化、可审计、可回退；
4. SQL、MES 或模型故障不阻塞控制链路；
5. 人工操作保存身份、原因和时间；
6. 不在仓库保存现场密钥、真实点位和未脱敏数据；
7. 不直接写 PLC、不自动停机、不修改调度；
8. 自动控制联动必须另立安全项目。
