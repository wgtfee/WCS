# EMS / RGV 统一调度第十阶段测试方案

## 1. 测试目标

验证生产可观测性不会改变前九阶段的确定性控制行为，并验证 Trace、Metrics、三方一致性、健康评分、配置快照和回滚安全边界。

## 2. Core 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportObservabilityTests.cs
```

### 2.1 Trace 与指标

场景：创建一次派单操作并成功完成。

验证：

- 最近 Trace 中包含 RequestId 和 VehicleId；
- Trace 成功状态正确；
- 操作总数增加；
- 失败数保持为零；
- 操作类型为 Dispatch。

### 2.2 三方位置差异

场景：数据库车辆在 N1，运行车辆在 N2。

验证：

- 生成 `VehiclePositionMismatch`；
- 运行车辆仍保持 N2；
- 不自动覆盖数据库或内存；
- 关闭一致性报警时 AlarmCenter 不产生报警。

### 2.3 三方一致

场景：数据库和运行车辆快照完全相同，无活动任务、路权和真实 PLC 驱动。

验证：

- 报告 `IsConsistent=true`；
- 差异列表为空。

### 2.4 健康报警自引用保护

场景：AlarmCenter 中存在 `TRANSPORT_HEALTH` 报警。

验证：

- Alarm 健康组件忽略自身报警；
- Alarm 组件评分保持 100；
- 健康状态能够恢复。

### 2.5 配置快照完整性

场景：创建包含运行配置、整定参数、站点和单轨定义的基线快照。

验证：

- 所有配置族均被保存；
- 版本号保持；
- 快照可从 Journal 查询。

### 2.6 回滚版本冲突

场景：提交旧运行配置版本执行回滚。

验证：

- 回滚被拒绝；
- 未创建安全快照；
- 当前配置不改变。

### 2.7 成功回滚

场景：当前配置版本与请求一致，回滚到历史快照。

验证：

- 自动创建安全快照；
- 运行配置和整定参数应用目标值；
- 新版本单调增加；
- 返回安全快照 ID。

## 3. Host 编译与接口测试

验证以下项目 Release 编译：

```text
src/Wcs.Host/Wcs.Host.csproj
```

启动后检查：

```text
GET  /health/live
GET  /health/ready
GET  /metrics
GET  /api/transport/observability/summary
GET  /api/transport/observability/health
GET  /api/transport/observability/traces
POST /api/transport/observability/consistency/inspect
```

预期：

- `/health/live` 始终反映进程存活；
- `/health/ready` 包含 TransportHealthState 和 TransportHealthScore；
- `/metrics` 返回 Prometheus 格式；
- API 响应头包含 `X-Trace-Id`；
- 诊断接口不改变车辆、任务和路权。

## 4. OpenTelemetry 测试

### 4.1 Prometheus

配置：

```json
"EnablePrometheusEndpoint": true
```

验证 `/metrics` 包含：

```text
wcs_transport_operations
wcs_transport_operation_failures
wcs_transport_operation_duration
wcs_transport_queue_wait
wcs_transport_plc_response
wcs_transport_consistency_issues
```

具体名称可能按 OpenTelemetry Prometheus 规范转换点号和单位。

### 4.2 OTLP

配置 Collector：

```json
"EnableOtlpExporter": true,
"OtlpEndpoint": "http://localhost:4317"
```

验证：

- Host 在 Collector 可用时发送 Trace 和 Metrics；
- Collector 不可用时调度和 PLC 命令仍继续；
- 日志记录导出异常但 Host 不退出。

## 5. 一致性故障注入

分别注入：

- 数据库缺少车辆；
- 内存缺少车辆；
- 车辆位置不一致；
- 活动执行缺失；
- 路权预留缺失；
- PLC 离线；
- PLC 位置不一致；
- PLC 当前命令不一致。

验证差异类型、严重级别、报警和健康评分。

## 6. 配置回滚治理测试

1. 使用用户 A 申请 `ChangeConfiguration`，目标为 SnapshotId。
2. 用户 A 尝试审批，必须失败。
3. 用户 B 审批。
4. 用户 C 或有执行权限的用户执行回滚。
5. 同一 OperationId 再执行一次，必须失败。
6. 检查审计记录包含目标快照、安全快照和执行结果。

## 7. Desktop 测试

编译：

```text
src/Wcs.Desktop/Wcs.Desktop.csproj
```

检查：

- 菜单出现“可观测性与一致性”；
- 页面可显示健康组件、差异、Trace、指标和快照；
- 健康评估和三方巡检按钮可用；
- 页面不存在配置回滚和 PLC 写入按钮；
- Host 不可用时页面显示错误但 Desktop 不崩溃。

## 8. 性能与降级

- 连续记录 10000 次操作，内存 Trace 仅保留最近 5000 条；
- 一致性巡检并发触发时通过信号量串行执行；
- Prometheus 抓取不阻塞调度线程；
- Collector 离线不阻塞控制闭环；
- 后台巡检单次失败不会终止 Host。

## 9. CI 验收

Windows CI 必须完成：

```text
dotnet restore
dotnet build Wcs.Core Release
dotnet test Wcs.Core.Tests Release
dotnet build Wcs.Host Release
dotnet build Wcs.Desktop Release
```

验收条件：

- 0 个编译错误；
- 全部测试通过；
- 没有 Avalonia XAML 编译错误；
- OpenTelemetry 包恢复成功；
- `develop` 更新，`main` 不变。
