# AnomalyEngine v3.6 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | AnomalyEngine v3.6 |
| 主题 | 资产健康根因关联与异常传播 |
| 分支 | `feature/anomaly-root-cause-propagation-v3-6` |
| PR | #29 |
| 仓库级验收基线 | `4c559fdcd045a69597a6246bdfd626fcc681dfec` |
| 状态 | 15 项矩阵已全绿，进入最终文档复验与合并流程 |
| 现场状态 | 不代表真实根因图、权限、实车或正式投产已经验收 |

## 2. 生产安全默认

仓库 `appsettings.Production.json` 保持：

```json
{
  "AssetHealthRootCause": {
    "Enabled": false,
    "AllowCycles": false,
    "Graph": {
      "Version": "",
      "Source": "",
      "ApprovedBy": "",
      "ApprovedAtUtc": null,
      "Nodes": [],
      "Edges": []
    }
  }
}
```

因此代码合并和部署不会自动启用根因分析，也不会把 CI 示例拓扑带到生产。

## 3. 启用前置条件

生产启用前必须完成：

1. v3.5 健康事件治理已经稳定运行；
2. 现场资产 `AssetId` 与图 `EntityId` 完整映射；
3. 设备、工艺和实施人员共同确认 Upstream → Downstream 方向；
4. Graph Version、Source、ApprovedBy、ApprovedAtUtc 完整；
5. 节点、边、边权、环路和容量经过离线校验；
6. SQL 三张表和索引创建成功；
7. 只读 API 和复核 API 接入项目身份与授权；
8. 通过回放、沙箱、负载和 Host 重启测试；
9. 生产发布、回退和现场验收单完成签署。

## 4. 功能验收范围

- 版本化根因图；
- GraphHash；
- 同版本不同 Hash 拒绝；
- 时间窗口关联；
- 上游候选搜索；
- 最短传播路径；
- RootCause、Intermediate、Symptom；
- Coverage、Topology、Temporal、Severity 解释；
- AnalysisId 幂等；
- SQL 图版本、分析快照、复核 Journal；
- Confirmed、Rejected、Supplemented；
- Host 重启恢复；
- 状态、图、分析、事件最新分析和复核 API。

## 5. 最终专项验收证据

```text
Workflow: WCS Asset Health Root Cause #9
Run ID: 30333201098
Source SHA: 4c559fdcd045a69597a6246bdfd626fcc681dfec
Artifact: wcs-asset-health-root-cause-9
Digest: sha256:44688aa44d8710b24c1372b9dcf0dccc53f9e7165e94353d7ed034f8860194eb
```

该运行验证：

- Core 和 Host 编译；
- 根因专项单元测试；
- SQL 建表和索引；
- 三节点根因排序与两级传播；
- AnalysisId 幂等；
- Confirmed 和 Supplemented Review Journal；
- Graph=1、Analysis=1、Review=2 精确计数；
- Host 重启恢复。

重启后的接口就绪采用有界重试，但没有删除、跳过或放宽任何业务、SQL、传播和恢复断言。

## 6. 最终完整矩阵

| 工作流 | 运行号 | 状态 |
|---|---:|---|
| WCS Asset Health Root Cause Compile | #19 | success |
| WCS Asset Health Root Cause | #9 | success |
| WCS Windows CI | #252 | success |
| WCS End-to-End Load | #180 | success |
| WCS PLC Telemetry Storage Load | #66 | success |
| WCS PLC Anomaly Engine Load | #192 | success |
| WCS PLC Anomaly Engine Soak | #175 | success |
| WCS Anomaly Fusion Load | #77 | success |
| WCS Anomaly Fusion Bridge E2E | #69 | success |
| WCS Transport Cycle Analysis | #71 | success |
| WCS Anomaly Health Scoring | #57 | success |
| WCS Anomaly Health Scoring SQL | #33 | success |
| WCS Asset Health Governance Compile | #21 | success |
| WCS Asset Health Governance | #26 | success |
| WCS One Hour Soak Load | #146 | success |

基线 `4c559fdcd045a69597a6246bdfd626fcc681dfec` 的 15 项工作流全部成功。

## 7. 安全边界验收

- [x] 默认 `Enabled=false`；
- [x] Production 图为空；
- [x] 默认 `AllowCycles=false`；
- [x] 无 PLC 写入；
- [x] 无设备停止命令；
- [x] 无任务取消；
- [x] 无车辆选择、路线、路权或派单修改；
- [x] SQL 或分析失败不阻塞控制链路；
- [x] Confidence 明确不是故障概率；
- [x] Supplemented 不修改活动图；
- [x] 图变更必须升级版本并审批；
- [x] LoadTest API 仅在 LoadTest 环境开放。

## 8. 项目级未完成事项

以下事项不由仓库 CI 替代：

- 真实设备、部件、信号、任务、工位和区段拓扑；
- 真实边方向和权重确认；
- 设备、工艺和生产审批；
- 身份认证、角色权限和审计策略；
- 现场 SQL 账号、备份和保留策略；
- 实际事件传播窗口和置信门槛；
- MES、维修系统或 Desktop 展示联调；
- 实车故障注入和正式投产签署。

## 9. 推荐生产启用顺序

```text
部署但保持 AssetHealthRootCause.Enabled=false
→ 导入已审批图
→ 校验 GraphHash 和 SQL 注册
→ 离线回放
→ 沙箱只读启用
→ 人工抽样复核候选和传播链
→ 接入身份权限
→ 项目变更审批
→ 生产只读启用
```

不得从“部署”直接跳到自动控制联动。

## 10. 回退

首选回退：

```text
AssetHealthRootCause__Enabled=false
```

关闭后：

- 停止新增分析；
- 保留图版本、分析和 Review Journal；
- 不影响 v3.5 健康事件和 MES Outbox；
- 不影响 v3.4 健康历史、Fusion、PLC、任务和调度。

若图版本存在问题，发布经过审批的旧图版本配置；不得覆盖 SQL 中已注册的历史版本，也不得删除分析证据。

## 11. 最终合并条件

PR #29 只有同时满足以下条件才能标记 Ready 并 Squash 合入 `develop`：

- 最新 Head 的 15 项完整矩阵全部成功；
- v3.6 专项 SQL E2E 成功；
- 一小时 Soak 成功；
- 文档 00、21、39、43～45 与代码一致；
- PR 描述记录最终运行号、Artifact、Digest、安全边界和回退；
- 不存在通过删除测试、跳过重启或放宽 SQL 数量门槛实现的假绿。

本次最终证据文档提交会再次触发完整矩阵；只有该新 Head 再次全部成功后才能合并。

## 12. 当前结论

```text
代码实现：完成
专项 Core：通过
专项 SQL E2E：通过
Host 重启恢复：通过
生产默认与回退：完成
文档：完成
首轮 15 项矩阵：通过
最终证据文档复验：等待最新 Head 全绿
仓库级最终验收：待最终复验后合并
现场投产验收：未开始
```