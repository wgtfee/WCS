# AnomalyEngine v3.3 生产配置与最终验收记录

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 功能名称 | AnomalyEngine v3.3 — 多模型异常证据融合 |
| PR | `#26` |
| 目标分支 | `develop` |
| 验收代码基线 | `ca9619804294072df2d508742cad93ef65c92c69` |
| 配置收口分支 | `feature/anomaly-evidence-fusion-v3-3` |
| 默认运行状态 | 关闭、只读、不接入控制 |
| 验收日期 | 2026-07-27 |

本文是文档 31《多模型异常证据融合架构与运维手册》和文档 32《多模型异常证据融合测试与交付报告》的最终生产配置补充与签署依据。

## 2. 最终交付结论

v3.3 已完成以下收口：

- 多模型 Evidence 标准化、同来源去重和独立来源融合；
- Normal、Observe、Warning、Alarm 状态机；
- 连续升级、恢复滞回、TTL 和容量治理；
- PLC 异常生命周期 Bridge；
- 运输周期顺序、阶段耗时和总周期 Bridge；
- 有界异步入口和 Written/Read/Dropped/Pending 守恒指标；
- 只读状态与资产 API；
- 百万 Evidence、高基数、10 分钟和一小时持续回归；
- 生产安全配置、回退步骤和正式验收证据。

安全边界保持不变：v3.3 不写 PLC、不停止设备、不取消任务、不修改路权、不改变调度决策。

## 3. 配置文件收口

### 3.1 `appsettings.json`

基础配置已改为安全默认：

- `ConnectionStrings:WcsDb` 不再包含账号和密码；
- `Simulator:Enabled=false`；
- `Storage:Telemetry:Provider=Disabled`；
- Telemetry 耐久模式默认使用 `WriteAhead`；
- `AnomalyDetection:Enabled=false`；
- `AnomalyDetection:MachineLearning:Enabled=false`；
- `ManagementApiEnabled=false`；
- `TransportCycleAnalysis:Enabled=false`；
- `AnomalyFusion:Enabled=false`；
- `Rules`、`Profiles`、`Sources` 和 PLC 数组均为空；
- 自动备份默认关闭，配置和目录验收后再开启。

基础配置不再携带示例 PLC 地址、DB 块或开发数据库口令，避免生产环境因 ASP.NET Core 数组合并规则继承示例项目。

### 3.2 `appsettings.Development.json`

开发环境使用 Windows Integrated Security 示例连接串，不提交 SQL 账号密码；默认启用 Simulator，Telemetry 和自动备份保持关闭。开发人员可通过 User Secrets 或环境变量覆盖。

### 3.3 `appsettings.Production.json`

生产环境文件只提供安全覆盖，不提供任何生产秘密：

```json
{
  "ConnectionStrings": {
    "WcsDb": ""
  },
  "Simulator": {
    "Enabled": false
  },
  "Storage": {
    "Telemetry": {
      "Provider": "Disabled",
      "DurabilityMode": "WriteAhead"
    }
  },
  "AnomalyDetection": {
    "Enabled": false,
    "MachineLearning": {
      "Enabled": false,
      "ManagementApiEnabled": false
    }
  },
  "TransportCycleAnalysis": {
    "Enabled": false
  },
  "AnomalyFusion": {
    "Enabled": false
  }
}
```

## 4. 生产必须提供的外部配置

启动生产 Host 前，至少提供：

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__WcsDb=<生产 SQL Server 最小权限连接串>
Simulator__Enabled=false
```

按现场方案选择 Telemetry：

```text
Storage__Telemetry__Provider=Disabled | SqlServer | InfluxDb
Storage__Telemetry__DurabilityMode=WriteAhead
```

使用 InfluxDB 时，还必须通过 Secret、服务配置或受控环境变量提供：

```text
Storage__Telemetry__InfluxDb__Url
Storage__Telemetry__InfluxDb__Token
Storage__Telemetry__InfluxDb__Organization
Storage__Telemetry__InfluxDb__Bucket
```

生产机密不得写回 Git 仓库。

PLC 连接、DB 块、Tag、规则、ML Profile 和 Fusion Source 数组必须以完整环境文件或受控配置中心提供。不要依赖数组下标局部覆盖。

## 5. v3.3 启用顺序

正式升级时保持：

```text
AnomalyDetection:Enabled=false
AnomalyDetection:MachineLearning:Enabled=false
AnomalyDetection:MachineLearning:ManagementApiEnabled=false
TransportCycleAnalysis:Enabled=false
AnomalyFusion:Enabled=false
```

推荐启用顺序：

```text
升级并保持全部模型关闭
→ 验证 Host、SQL、PLC 和原调度功能
→ 测试环境启用上游检测器
→ 验证异常生命周期与误报率
→ 启用 AnomalyFusion 只读观察
→ 核对 Written/Read/Dropped/Pending
→ 观察完整生产周期
→ 审批配置版本
→ 小范围现场启用
```

任何阶段不得直接把 Fusion 状态接入自动控制。

## 6. 最终自动化验收证据

### 6.1 一小时系统 Soak

| 项目 | 结果 |
|---|---:|
| Workflow | `WCS One Hour Soak Load #75` |
| Run ID | `30247055238` |
| Artifact | `wcs-one-hour-soak-75` |
| Artifact ID | `8646936066` |
| HTTP 请求 | 15,723,968 |
| 平均吞吐 | 4,367.19 RPS |
| HTTP 失败 | 0 |
| P50 / P95 / P99 | 5 / 9 / 25 ms |
| SignalR 连接 | 100 / 100 |
| SignalR 消息 | 3,855,400 |
| SignalR 错误 | 0 |
| 初始 / 最终 / 峰值 RSS | 166.97 / 352.23 / 538.28 MB |
| Q4-Q2 | 26.44 MB |
| 最后 15 分钟斜率 | -0.477 MB/分钟 |
| GC 后托管存活内存 | 45.87 MB |
| GC 后进程 RSS | 319.87 MB |
| 队列峰值 / 最终 | 2 / 0 |
| DeviceStateLog | 1,980 |
| TaskRun | 5,694 |
| 结论 | 通过 |

最终验收使用进程硬预算、预热后平台增量、尾段斜率、GC 后托管存活量和队列排空联合判断，不使用冷启动 RSS 作为单一泄漏基线。

### 6.2 十分钟 PLC 异常持续回归

| 项目 | 结果 |
|---|---:|
| Workflow | `WCS PLC Anomaly Engine Soak #104` |
| Run ID | `30247055107` |
| Artifact | `wcs-plc-anomaly-soak-104` |
| Artifact ID | `8645678743` |
| 处理样本 | 631,200 |
| 激活 / 恢复 | 7,200 / 7,200 |
| SQL 生命周期 | 7,200 |
| Threshold / Consistency | 4,800 / 2,400 |
| 失败 / 抑制 | 0 / 0 |
| 最终活动异常 | 0 |
| 初始 / 最终 / 峰值 RSS | 151.35 / 281.90 / 428.96 MB |
| 尾段平台中位数增量 | 6.08 MB |
| 最后两分钟斜率 | 4.34 MB/分钟 |
| GC 后托管存活内存 | 12.47 MB |
| GC 后进程 RSS | 268.59 MB |
| 结论 | 通过 |

### 6.3 ML E2E 与完整矩阵

`WCS PLC Anomaly ML E2E #77` 的重跑 Job `89929185123` 已成功完成训练、Host 重启、活动模型重载、留出正常窗口、异常窗口、恢复窗口、SQL 生命周期、误报和内存日志验证。

同一验收基线下，下列矩阵均已通过：

- WCS Windows CI；
- WCS End-to-End Load；
- WCS PLC Anomaly Engine Load；
- WCS PLC Anomaly Engine Soak；
- WCS PLC Anomaly ML；
- WCS PLC Anomaly ML E2E；
- WCS PLC Anomaly ML Governance；
- WCS PLC Anomaly ML Version Throughput；
- WCS PLC Anomaly ML Context Peer；
- WCS Transport Cycle Analysis；
- WCS Anomaly Fusion Load；
- WCS Anomaly Fusion Bridge E2E；
- WCS One Hour Soak Load。

## 7. 生产验收门槛

### 一小时系统 Soak

| 指标 | 门槛 |
|---|---:|
| HTTP 请求 | ≥1,000,000 |
| HTTP 失败 | 0 |
| P95 | ≤500 ms |
| SignalR 错误 | 0 |
| 内存采样 | ≥3,300 |
| GC 前 RSS 增长 | ≤900 MB |
| 峰值 RSS | ≤1,200 MB |
| Q4-Q2 | ≤100 MB |
| 最后 15 分钟斜率 | ≤3 MB/分钟 |
| GC 后进程 RSS | ≤1,024 MB |
| GC 后托管存活内存 | ≤256 MB |
| 队列峰值 / 最终 | ≤100 / ≤25 |

### 十分钟异常 Soak

| 指标 | 门槛 |
|---|---:|
| 样本 | 631,200 |
| 激活 / 恢复 | 7,200 / 7,200 |
| SQL 生命周期 | 7,200 |
| 最终活动异常 | 0 |
| 峰值 RSS | ≤850 MB |
| 最终 RSS 增长 | ≤450 MB |
| 尾段平台中位数增量 | ≤32 MB |
| 最后两分钟斜率 | ≤15 MB/分钟 |
| GC 后托管存活内存 | ≤256 MB |
| GC 后进程 RSS | ≤650 MB |

## 8. 回退方案

Fusion 回退：

```text
AnomalyFusion__Enabled=false
→ 重启 Host
→ 验证 Fusion BackgroundService、PLC Bridge、Cycle Bridge 未运行
→ 核对原规则、ML、同群和周期检测仍独立工作
```

完整异常能力回退：

```text
AnomalyDetection__Enabled=false
AnomalyDetection__MachineLearning__Enabled=false
AnomalyDetection__MachineLearning__ManagementApiEnabled=false
TransportCycleAnalysis__Enabled=false
AnomalyFusion__Enabled=false
```

配置回退必须保留上一版本配置、模型目录、训练目录、Telemetry WAL/spool 和数据库备份。

## 9. 最终检查清单

- [x] 仓库不再包含开发 SQL 明文密码；
- [x] 基础配置默认关闭 Simulator；
- [x] 基础 PLC 和模型数组为空；
- [x] Production 配置不包含生产秘密；
- [x] Management API 默认关闭；
- [x] AnomalyFusion 默认关闭；
- [x] 百万 Evidence 测试通过；
- [x] PLC/Cycle Bridge E2E 通过；
- [x] 原异常高基数和十分钟 Soak 通过；
- [x] ML E2E 重跑通过；
- [x] 一小时系统 Soak 通过；
- [x] HTTP、SignalR、队列、SQL、GC 和日志证据完整；
- [x] 未接入自动设备控制；
- [ ] 现场生产连接串、PLC 点位、规则、Profile 和 Source 权重已审批；
- [ ] 现场联调和正式投产签署完成。

后两项属于现场实施和投产流程，不影响软件 v3.3 合入 `develop`。

## 10. 下一阶段边界

v3.4 将在最新 `develop` 基础上实现资产健康评分。v3.4 不得复用现有 `TransportObservability` 的系统级健康分数配置项，也不得在未完成新一轮安全评审前把健康评分用于自动控制。
