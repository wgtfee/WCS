# EMS/RGV API 与系统集成说明书

## 1. 目的

本文供 MES、WMS、第三方 WCS、Desktop 和实施工具集成使用，说明接口分组、幂等、认证、Trace、状态码和集成建议。

## 2. 通用约定

### 2.1 基础地址

```text
http(s)://{host}:{port}/api
```

### 2.2 数据格式

- 请求和响应：`application/json`
- 时间：ISO 8601 UTC
- 编码：UTF-8
- 枚举：默认使用名称或框架配置的序列化形式，集成前应通过 OpenAPI/实际响应确认

### 2.3 Trace

每次 HTTP 响应返回：

```text
X-Trace-Id
```

调用方应将 TraceId 写入业务日志，以便关联派单、路权、PLC 命令和报警。

### 2.4 幂等

运输任务由 `RequestId` 保证幂等。调用方必须：

1. 为同一业务运输任务持续使用相同 RequestId；
2. 网络超时重试时不得生成新 RequestId；
3. 业务明确重新创建任务时才使用新 RequestId。

### 2.5 状态码

| 状态码 | 含义 |
|---:|---|
| 200 | 查询或操作成功 |
| 201 | 已创建（如后续启用） |
| 400 | 参数错误 |
| 401 | 未认证 |
| 403 | 无权限或审批不满足 |
| 404 | 目标不存在 |
| 409 | 状态冲突、版本冲突、资源冲突或操作失败 |
| 500 | 未处理异常 |
| 503 | Readiness 未通过 |

## 3. 统一调度 API

基础路径：

```text
/api/transport
```

主要接口：

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/vehicles` | 查询统一车辆快照 |
| GET | `/executions` | 查询执行任务 |
| GET | `/reservations` | 查询活动路权 |
| GET | `/runtime-snapshot` | 查询持久化运行快照 |
| POST | `/dispatch` | 直接统一派单 |
| POST | `/recover` | 执行启动恢复协调 |
| POST | `/commands/dispatch` | 受控分发逻辑命令 |
| POST | `/executions/{requestId}/start` | 启动执行 |
| POST | `/executions/{requestId}/loaded` | 确认装载 |
| POST | `/executions/{requestId}/unloaded` | 确认卸载 |
| POST | `/executions/{requestId}/pause` | 暂停 |
| POST | `/executions/{requestId}/resume` | 恢复 |
| POST | `/executions/{requestId}/fault` | 标记故障 |
| POST | `/executions/{requestId}/cancel` | 取消 |
| POST | `/position-feedback` | 提交可信位置反馈 |
| GET | `/vehicles/{vehicleId}/commands` | 查询/提取车辆逻辑命令 |

生产系统优先使用生产队列 API，而不是由 MES 逐步调用执行状态接口。

## 4. 交通控制 API

基础路径：

```text
/api/transport/traffic
```

提供交通快照、资源、持有关系、等待关系、死锁检测和处置结果查询。自动释放接口必须遵守物理占用保护。

## 5. 优化 API

基础路径：

```text
/api/transport/optimization
```

提供充电站、充电计划、任务重分配、性能快照和充电评估。

## 6. 配置与治理 API

基础路径：

```text
/api/transport/administration
```

包含：

- 运行配置查询/保存；
- 治理操作申请；
- 审批/拒绝；
- 审计查询；
- Journal 查询；
- 配置应用和受控危险操作。

配置保存必须提交 ExpectedVersion。

危险操作建议流程：

```text
POST operation request
→ 独立审批人 approve
→ 执行接口携带 OperationId
→ 服务校验 OperationType/TargetId
→ 执行并记录审计
```

## 7. 驱动诊断 API

基础路径以实现 Controller 为准，主要能力包括：

- PLC 点位映射查询；
- Driver 诊断状态；
- 立即轮询；
- 三方驱动对账；
- 车辆信号探测。

驱动诊断接口不得作为 MES 业务接口使用。

## 8. 现场联调 API

基础路径：

```text
/api/transport/commissioning
```

主要能力：

- 导入 JSON/CSV/XLSX 点位表；
- 获取信号模板；
- 校验并应用点位；
- 单点读取；
- 受审批单点写入；
- 在线探测；
- 通信 Trace；
- 故障码字典；
- 恢复冲突刷新和处置；
- 命令补偿评估和执行。

## 9. 生产调度 API

基础路径：

```text
/api/transport/production
```

主要接口：

```text
GET  /tuning
PUT  /tuning
GET  /stations
PUT  /stations/{stationId}
POST /stations/{stationId}/runtime
GET  /single-track
PUT  /single-track/{sectionId}
GET  /queue
POST /queue
POST /queue/{requestId}/cancel
POST /queue/{requestId}/complete
POST /dispatch-cycle
GET  /dry-run
GET  /decisions
GET  /trends
POST /trends/capture
GET  /fault-takeover
POST /fault-takeover/evaluate
```

### 9.1 推荐入队请求

上层系统提交：

- RequestId；
- SourceNodeId；
- DestinationNodeId；
- DestinationStationId；
- Priority；
- ProductionOrderPriority；
- DeadlineAtUtc；
- AllowedVehicleKinds；
- RequiredCapabilities；
- IsRecoveryTask。

### 9.2 查询队列

队列查询使用纯快照，不刷新优先级或清理任务。

## 10. 可观测性 API

基础路径：

```text
/api/transport/observability
```

```text
GET  /summary
GET  /health
POST /health/evaluate
GET  /metrics
GET  /traces
GET  /consistency/latest
GET  /consistency/reports
POST /consistency/inspect
GET  /configuration-snapshots
POST /configuration-snapshots
POST /configuration-snapshots/{snapshotId}/rollback
GET  /report/export
```

Prometheus 文本指标使用根路径：

```text
/metrics
```

## 11. 韧性 API

基础路径：

```text
/api/transport/resilience
```

提供生产就绪、运行基线、逻辑备份、下载、校验、恢复准备、隔离演练和报告导出。

恢复准备只创建待审批配置快照，不直接应用。

## 12. 仿真 API

基础路径：

```text
/api/transport/simulation
```

```text
GET  /summary
POST /scenarios/current
POST /scenarios/history
POST /runs
POST /comparisons
POST /optimizations
POST /capacity-benchmarks
POST /acceptance-reports
GET  /runs
GET  /comparisons
GET  /optimizations
GET  /capacity-benchmarks
GET  /acceptance-reports
GET  /report/export
```

仿真结果不会修改生产状态或参数。

## 13. Health API

```text
GET /health/live
GET /health/ready
GET /health
```

- live：进程存活；
- ready：运输健康和生产就绪；
- ready 未评估、Critical 或 Error 时返回不可用。

## 14. MES 集成建议

### 14.1 创建任务

1. MES 生成全局唯一 RequestId；
2. POST 到生产队列；
3. 保存 WCS 返回结果；
4. 超时使用相同 RequestId 重试；
5. 轮询队列/执行状态或通过既有推送机制接收状态。

### 14.2 取消任务

取消前 MES 应确认当前任务阶段。WCS 返回成功只代表取消请求已接受，不代表物理路权立即释放。

### 14.3 错误处理

- 409 WaitingForVehicle/Traffic/Station：业务可继续等待，不应创建新 RequestId；
- 409 版本冲突：重新读取最新配置后由人员确认；
- 503：停止创建新任务，等待 Readiness 恢复；
- 网络超时：使用同 RequestId 查询和重试。

## 15. 安全要求

1. 生产写接口必须使用认证和最小权限。
2. PLC 单点写、恢复冲突处置、命令补偿和配置回滚必须走审批。
3. 报告导出和备份下载需认证。
4. 不得向普通业务系统开放联调写接口。
5. API 网关或 IIS 应限制来源网络、请求大小和调用频率。
