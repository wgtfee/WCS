# AnomalyEngine v3.7 生产配置与最终验收记录

## 1. 验收对象

| 项目 | 内容 |
|---|---|
| 版本 | AnomalyEngine v3.7 |
| 能力 | 维修检查建议、反馈闭环、MES 工单关联、指标和训练标签候选 |
| 目标分支 | `develop` |
| 生产默认 | 关闭 |
| 控制属性 | 只读诊断与人工辅助决策 |

本文记录仓库级代码、配置和自动化验收，不代表真实现场维修 SOP、MES 工单接口、权限或正式投产已验收。

## 2. 生产默认配置

```json
{
  "AssetHealthMaintenance": {
    "Enabled": false,
    "EvaluationIntervalSeconds": 30,
    "MaximumRules": 10000,
    "MaximumItemsPerRecommendation": 100,
    "MaximumRecommendationsQueryCount": 1000,
    "MinimumRootCauseConfidence": 0.25,
    "RecommendationRetentionHours": 8760,
    "MaintenanceIntervalSeconds": 3600,
    "MaintenanceBatchSize": 2000,
    "RuleSet": {
      "Version": "",
      "Source": "",
      "ApprovedBy": "",
      "ApprovedAtUtc": null,
      "Rules": []
    }
  }
}
```

仓库不提交现场规则、人员、工单号、工具、备件、SOP 或安全说明。

## 3. 生产启用前置条件

- [ ] v3.5 活动健康事件生命周期已验收；
- [ ] v3.6 根因图、分析和人工复核已验收；
- [ ] 现场资产、部件和 RootCauseNodeId 已确认；
- [ ] 维修规则由设备、工艺、安全和维修人员审核；
- [ ] RuleSet Version、Source、ApprovedBy、ApprovedAtUtc 完整；
- [ ] 检查项、工具、备件和安全注意事项经过现场确认；
- [ ] SQL 四表、索引、备份和保留期已确认；
- [ ] API 身份认证、角色授权和审计已接入；
- [ ] MES 工单号字段及责任边界已确认；
- [ ] LoadTest 专用接口在生产返回 404；
- [ ] 回退方案已经演练。

## 4. 启用顺序

```text
保持 AssetHealthMaintenance.Enabled=false
→ 导入规则并离线校验
→ 记录 RuleSetHash
→ 设备/工艺/安全/维修联合审批
→ 沙箱或影子环境验证
→ AssetHealthMaintenance.Enabled=true
→ 只读观察建议与无匹配比例
→ 验证反馈、工单字段、指标和标签审批
→ 项目级签署
```

首次投产不得同时启用新的根因图、维修规则和主动 MES 写入。

## 5. 现场规则要求

每条规则必须至少包含：

- 唯一 RuleId；
- 精确 RootCauseNodeId 或 RootCauseKind；
- 最低事件等级；
- 明确标题；
- 至少一个检查项；
- 适用的安全注意事项。

建议补充部件、工具、备件、优先级和预计时长。不得把未经审核的生成式文本直接作为生产规则。

## 6. 权限矩阵

建议至少区分：

| 操作 | 角色建议 |
|---|---|
| 查看规则和建议 | 运维、维修、生产管理 |
| Accepted / Rejected | 计划或维修负责人 |
| Repaired / NoFaultFound / FalsePositive | 授权维修人员 |
| 训练标签 Approved / Rejected | 模型治理或质量负责人 |
| 修改规则版本 | 设备/工艺/安全联合审批 |

Actor 应优先来自认证身份，禁止共享账号或匿名生产写入。

## 7. SQL 验收

应确认：

- `Wcs_AssetHealthMaintenanceRuleSetVersion` 可追溯规则版本；
- `Wcs_AssetHealthMaintenanceRecommendation` RecommendationId 唯一；
- `Wcs_AssetHealthMaintenanceFeedbackJournal` FeedbackId 唯一；
- `Wcs_AssetHealthMaintenanceTrainingLabel` CandidateId 唯一；
- 同 Version 不同 Hash 被拒绝；
- 反馈不覆盖原始 RecommendationJson；
- 已审批标签不会自动改变活动模型；
- 保留期清理不删除待审批标签。

## 8. 仓库级专项验收

```text
Workflow: WCS Asset Health Maintenance #3
Run ID: 30344513405
Source SHA: 620eb179bfdb6d349487ebe931784f8c220e1349
Artifact: wcs-asset-health-maintenance-3
Digest: sha256:b572875eadfbeafdb8e413ef8ed6ac7f42400ede4b189c0c6ae85987c479675f
Conclusion: success
```

专项验证了：

- RuleSet=1；
- Recommendation=1；
- Feedback=2；
- TrainingLabel=1；
- 建议和反馈重复提交幂等；
- Accepted 和 Repaired 生命周期；
- MES-WO-1001 与 technician-a 关联；
- 维修后健康分 92；
- fault-confirmed 标签人工 Approved；
- Host 重启恢复。

## 9. 最终矩阵

最新文档 Head 必须通过 47 号测试报告所列完整矩阵。最终运行号和 Head SHA 在 PR 转 Ready 前补录。

## 10. 回退

紧急回退：

```text
AssetHealthMaintenance__Enabled=false
```

回退效果：

- 停止后台生成新建议；
- 保留规则版本、建议、反馈和标签；
- 不影响 v3.5、v3.6、PLC、任务和调度；
- 已存在建议继续可读；
- 不自动关闭 MES 工单。

## 11. 明确不属于本次仓库级验收

- 真实现场维修规则准确性；
- 现场安全作业许可；
- 真实 MES 工单创建和关闭；
- 人员组织和权限审批；
- 备件库存准确性；
- 维修建议自动停机；
- 训练标签自动训练或激活模型；
- 正式投产签署。

## 12. 安全结论

v3.7 当前实现满足以下仓库级边界：

```text
默认关闭
+ Production 空规则集
+ 只接受已复核根因
+ 无规则不生成建议
+ 反馈追加审计
+ 标签独立审批
+ 无 PLC 写入
+ 无停机
+ 无任务取消
+ 无调度修改
```

只有最新 Head 完整矩阵全部成功后，才能将 PR 标记 Ready 并合入 `develop`。
