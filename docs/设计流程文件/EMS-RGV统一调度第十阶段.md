# EMS / RGV 统一调度第十阶段：生产可观测性与一致性保障

## 1. 阶段目标

第十阶段在前九阶段确定性调度、交通控制、PLC 驱动和生产队列之上，建立不干扰控制闭环的生产诊断能力：

- API、派单、执行、PLC 命令和确认使用同一 TraceId 追踪；
- 输出调度次数、失败次数、处理耗时、排队耗时和 PLC 响应耗时；
- 定时比较数据库、运行内存和 PLC 可信状态；
- 将一致性、车辆、PLC、队列和报警汇总为健康评分；
- 支持生产配置快照、版本保护和审批后回滚；
- 监控系统故障不得阻塞派单、执行或 PLC 通信。

## 2. 安全边界

1. 可观测性组件只读取运行状态，不自动修改车辆位置、执行状态、路权或 PLC。
2. 一致性差异只生成报告、指标和报警。
3. 配置快照创建不改变运行状态。
4. 配置回滚必须使用 `ChangeConfiguration` 独立审批，审批目标必须是快照 ID。
5. 回滚前自动创建安全快照；版本发生变化时拒绝回滚。
6. Trace、Metrics、Prometheus 或 OTLP 后端不可用时，控制闭环继续运行。

## 3. 总体结构

```text
HTTP Request
    ↓ X-Trace-Id / ASP.NET Activity
ObservableUnifiedTransportDispatchEngine
    ↓
ReliableTransportProductionDispatchService
    ↓
ExecutionEngine
    ↓
ObservableTransportCommandDispatcher
    ↓
ReliableTransportVehicleDriver
    ↓
PLC / EMS Controller
```

统一使用：

```text
ActivitySource = Wcs.Transport
Meter          = Wcs.Transport
ServiceName    = wcs-runtime-engine
```

## 4. 链路追踪

关键 Span：

- `transport.dispatch`
- `transport.plc.command`
- `transport.consistency.inspect`
- `transport.health.evaluate`
- `transport.configuration.snapshot`
- `transport.configuration.rollback`

每条记录包含：

- TraceId、SpanId、ParentSpanId；
- RequestId、VehicleId；
- 操作类型和操作名；
- 成功状态、耗时和错误；
- 车辆、路权、命令、版本等标签。

进程内保留最近 5000 条关键链路，便于现场在没有 Collector 时诊断。

## 5. 指标

自定义指标：

```text
wcs.transport.operations
wcs.transport.operation.failures
wcs.transport.operation.duration
wcs.transport.queue.wait
wcs.transport.plc.response
wcs.transport.consistency.issues
```

同时采集 ASP.NET Core、HttpClient 和 .NET Runtime 指标。

默认启用 Prometheus 抓取端点：

```text
/metrics
```

OTLP 默认关闭，可配置 Collector 地址后启用。

## 6. 三方一致性巡检

### 6.1 比较范围

数据库与运行内存：

- 车辆是否存在；
- 车辆位置和在线状态；
- 活动执行任务及车辆、状态；
- 活动路权预留。

运行内存、数据库与 PLC：

- PLC 访问器和设备在线状态；
- PLC 可信节点与运行位置；
- 数据库活动命令与 PLC 当前/确认命令。

### 6.2 差异级别

```text
Information
Warning
Error
Critical
```

活动任务、物理路权、PLC 位置或命令不一致属于高优先级问题。

### 6.3 处理原则

- 定时巡检默认 30 秒；
- 报告保存到 `Wcs_TransportJournal`；
- 可配置是否触发 `TRANSPORT_CONSISTENCY` 报警；
- 巡检不自动修正任何状态。

## 7. 健康评分

健康评分包含五个组件：

- Consistency
- Fleet
- PLC
- Queue
- Alarm

默认阈值：

```text
Score >= 80       Healthy
50 <= Score < 80  Degraded
Score < 50        Unhealthy
```

健康评分生成 `TRANSPORT_HEALTH` 报警，但计算报警组件时排除 `TRANSPORT_HEALTH` 和 `TRANSPORT_CONSISTENCY`，避免自引用导致无法恢复。

就绪探针 `/health/ready` 会返回运输健康状态和评分；存活探针 `/health/live` 不受业务健康影响。

## 8. 配置快照与回滚

快照内容：

- TransportRuntimeConfiguration
- TransportProductionTuningOptions
- 生产站点定义
- 单轨区段定义
- 创建人、原因和时间

回滚顺序：

1. 校验目标快照存在；
2. 校验运行配置版本；
3. 校验整定参数版本；
4. 创建回滚前安全快照；
5. 应用运行配置；
6. 应用整定参数；
7. 应用站点和单轨定义；
8. 任一步异常时尝试恢复安全快照。

配置回滚不会绕过现有治理服务。

## 9. Host API

基础路径：

```text
/api/transport/observability
```

接口：

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
```

其中 `/metrics` 是业务指标 JSON；Prometheus 抓取使用 Host 根路径 `/metrics`。

## 10. Desktop

新增“可观测性与一致性”页面，包含：

- 健康组件；
- 三方一致性差异；
- 链路追踪；
- 聚合指标；
- 配置快照。

页面只允许刷新、健康评估和三方巡检，不提供配置回滚按钮。

## 11. 持久化

继续复用 `Wcs_TransportJournal`，新增类别：

```text
ConsistencyReport
ConfigurationSnapshot
ObservabilityHealth
```

无需新增数据库表或数据库迁移。

## 12. 阶段验收标准

- Core、Host、Desktop 在 Windows CI 编译通过；
- 全部 Core 测试通过；
- 派单和 PLC 命令产生 Trace 与指标；
- 三方差异可检测且不修改运行状态；
- 健康评分可恢复，不受自身报警污染；
- 配置回滚受版本和审批保护；
- `/metrics`、`/health/ready`、`/health/live` 可访问；
- `main` 不发生变更。
