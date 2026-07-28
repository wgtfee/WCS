# AnomalyEngine v3.6 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 版本 | AnomalyEngine v3.6 |
| 主题 | 资产健康根因关联与异常传播 |
| 分支 | `feature/anomaly-root-cause-propagation-v3-6` |
| PR | #29 |
| 状态 | Draft，等待最新文档提交完整矩阵全绿 |
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

## 5. 专项验收证据

首次完整成功：

```text
Workflow: WCS Asset Health Root Cause #1
Run ID: 30332417724
Source SHA: 3f340d836a8827d39a79b3ecd8b690c4dfc38d84
Artifact: wcs-asset-health-root-cause-1
Digest: sha256:605e9dfc0a00a4dd097d901d035244709a8fe0aa9e478c1bf59d728028317b80
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

后续单次状态请求竞态已通过工作流就绪重试修复，且未放宽任何业务或 SQL 断言。

## 6. 最终完整矩阵

最新文档提交必须全部通过以下工作流：

| 工作流 | 最终状态/运行号 |
|---|---|
| WCS Asset Health Root Cause Compile | 待最终补录 |
| WCS Asset Health Root Cause | 待最终补录 |
| WCS Windows CI | 待最终补录 |
| WCS End-to-End Load | 待最终补录 |
| WCS PLC Telemetry Storage Load | 待最终补录 |
| WCS PLC Anomaly Engine Load | 待最终补录 |
| WCS PLC Anomaly Engine Soak | 待最终补录 |
| WCS Anomaly Fusion Load | 待最终补录 |
| WCS Anomaly Fusion Bridge E2E | 待最终补录 |
| WCS Transport Cycle Analysis | 待最终补录 |
| WCS Anomaly Health Scoring | 待最终补录 |
| WCS Anomaly Health Scoring SQL | 待最终补录 |
| WCS Asset Health Governance Compile | 待最终补录 |
| WCS Asset Health Governance | 待最终补录 |
| WCS One Hour Soak Load | 待最终补录 |

最终矩阵以最新 Head SHA 为准；不得使用被后续代码或文档提交替代的旧运行作为最终合并依据。

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

## 12. 当前结论

```text
代码实现：完成
专项 Core：通过
首次专项 SQL E2E：通过
工作流重启就绪增强：完成
生产默认与回退：完成
文档：完成
最新 Head 完整矩阵：执行中
仓库级最终验收：待全绿
现场投产验收：未开始
```
