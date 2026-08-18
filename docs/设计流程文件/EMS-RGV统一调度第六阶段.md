# EMS / RGV 统一调度第六阶段

## 1. 阶段目标

第六阶段将前五阶段的调度内核推进到可受控接入现场的运维形态，覆盖：

1. 调度配置版本化持久化；
2. 充电站、交通资源、车辆和驱动参数统一配置；
3. 乐观并发控制，防止多人配置覆盖；
4. 危险操作权限、独立审批和一次性执行；
5. 充电、换车、交通事件、性能和车辆状态运行日志落库；
6. 真实 EMS/RGV 驱动通道契约；
7. 命令序号、幂等确认、心跳和确认超时；
8. Host 管理 API 与 Desktop 只读运维页面。

---

## 2. 配置模型

`TransportRuntimeConfiguration` 包含：

- `ChargingPolicy`
- `TrafficResources`
- `ChargingStations`
- `Vehicles`
- `Drivers`
- `Version`
- `UpdatedBy`
- `UpdatedAtUtc`

驱动配置只保存协议和端点元数据，不在 Core 中硬编码 PLC 地址：

```text
DriverId
VehicleKind
Protocol
Endpoint
StationName
PollIntervalMs
CommandTimeoutMs
Parameters
```

现场可在 `Parameters` 中配置 DB 块、起始地址、标签名或厂商控制器参数。

### 2.1 乐观版本控制

保存配置必须提交 `ExpectedVersion`。

```text
客户端读取 Version=5
        ↓
客户端提交 ExpectedVersion=5
        ↓
数据库仅在当前 Version=5 时更新为 Version=6
```

如果其他用户已经保存为 Version=6，旧客户端返回版本冲突，不能覆盖新配置。

### 2.2 配置应用

配置保存成功后立即应用：

- 充电策略热更新；
- 交通资源新增或覆盖；
- 充电站新增或覆盖；
- 车辆能力和类型更新；
- 新车辆以 Offline 状态注册，等待实际驱动状态接管。

为了安全，配置应用不会自动删除正在被占用的交通资源，也不会中断已有充电计划。

---

## 3. 权限与双人确认

### 3.1 权限声明

```text
transport.admin.read
transport.config.change
transport.task.reassign
transport.traffic.force-release
transport.battery.override
transport.driver.manual-command
transport.operation.approve
```

Host 从认证用户 Claims 中读取 `permission` 或 `permissions`。请求体中的用户名不作为可信身份。

如果用户属于 `Administrator` 或 `WcsAdministrator` 角色，可映射为全部运输管理权限。

### 3.2 受控操作

```text
ChangeConfiguration
ReassignTask
ForceReleaseTraffic
OverrideLowBattery
SendManualDriverCommand
```

以下操作要求申请人与独立审批人是不同账号：

- 修改调度配置；
- 故障车辆任务转移；
- 强制释放交通资源；
- 手动发送车辆命令。

### 3.3 状态机

```text
PendingApproval
      ↓ 独立审批
Approved
      ↓ 一次性开始执行
Executing
      ↓
Executed / Failed
```

其他终态：

```text
Rejected
Expired
```

`BeginExecutionAsync` 会原子地把状态从 `Approved` 改为 `Executing`，相同审批号不能重复执行。

### 3.4 审计

以下动作全部写入审计表：

- Requested
- Approved
- Rejected
- Expired
- ExecutionStarted
- ExecutionCompleted
- ExecutionFailed

审计包含操作号、操作人、目标、结果、说明和 UTC 时间。

---

## 4. SQL Server 表

第六阶段新增四张表：

```text
Wcs_TransportConfiguration
Wcs_TransportJournal
Wcs_TransportGovernedOperation
Wcs_TransportAudit
```

### 4.1 配置表

保存当前有效配置 JSON、版本号、更新人和更新时间。

### 4.2 运行日志表

统一记录：

```text
ChargingPlan
TaskReassignment
TrafficIncident
PerformanceSnapshot
DriverState
```

业务记录使用 `Category + RecordId` 幂等更新；性能快照按分钟归档。

### 4.3 审批与审计表

审批表保存完整状态机快照；审计表只追加，不覆盖历史记录。

每次数据库操作创建独立 `SqlSugarClient`，避免多个后台服务共享非线程安全上下文。

---

## 5. 真实设备驱动契约

### 5.1 分层

```text
TransportCommandDispatcher
        ↓
ReliableTransportVehicleDriver
        ↓
ITransportDriverChannel
        ↓
S7 / OPC UA / Modbus / TCP / 厂商 EMS 控制器适配器
```

`ITransportDriverChannel` 只定义：

```text
WriteCommandAsync
ReadStateAsync
```

具体 PLC DB 地址和标签映射留给 Infrastructure 或现场项目实现。

### 5.2 命令帧

```text
CommandId
RequestId
VehicleId
Sequence
CommandType
TargetNodeId
CreatedAtUtc
```

每辆车使用独立单调递增序号，并通过 `SemaphoreSlim` 保证同一车辆命令串行下发。

### 5.3 幂等确认

发送前先读取设备状态。如果设备已经确认同一个 `CommandId`，驱动直接返回已有结果，不重复写 PLC。

### 5.4 心跳和确认超时

- 心跳超过 `HeartbeatTimeout`，车辆映射为 Offline；
- 命令超过 `CommandAcknowledgementTimeout` 未确认，抛出超时异常；
- 上层 `TransportCommandDispatcher` 按原有重试策略处理。

---

## 6. Host API

基础路径：

```text
/api/transport/administration
```

只读接口：

```text
GET /configuration
GET /operations
GET /audits
GET /journal
```

审批接口：

```text
POST /operations
POST /operations/{operationId}/approve
POST /operations/{operationId}/reject
```

受控执行接口：

```text
PUT  /configuration/{operationId}
POST /operations/{operationId}/execute/traffic/{ownerId}/force-release
POST /operations/{operationId}/execute/driver/{vehicleId}/command
```

故障换车接口保留原地址，但必须提交审批号：

```text
POST /api/transport/optimization/executions/{requestId}/reassign
```

请求增加：

```json
{
  "reason": "车辆驱动故障",
  "startImmediately": true,
  "operationId": "已完成独立审批的操作号"
}
```

充电站直接新增和删除接口从第六阶段起返回冲突，要求统一走版本化配置。

---

## 7. Desktop 页面

新增菜单：

```text
配置与审计
```

页面包含：

- 当前配置版本；
- 交通资源、充电站、车辆和驱动数量；
- 驱动端点和超时参数；
- 待审批和失败操作；
- 审计记录；
- 运行日志 JSON。

Desktop 默认只读，不提供强制释放、手动命令或绕过审批按钮。

---

## 8. 安全边界

第六阶段不假设请求体身份可信。

危险接口在没有认证 Claims 时返回 401。项目接入 JWT、Windows Authentication、OIDC 或企业统一认证后，只需把权限写入 Claims，无需重写调度治理服务。

真实 PLC 驱动只提供可靠协议框架，不猜测现场 DB 地址、确认位和位置编码。现场映射必须经过 PLC 工程师确认后在 `ITransportDriverChannel` 实现。

---

## 9. 后续阶段

第七阶段建议处理：

- 现场 S7-1500 EMS/RGV 通道实现；
- 驱动配置加密和密钥管理；
- JWT/Windows Authentication 正式接入；
- 班次日报和瓶颈趋势；
- 仿真器延迟、丢包、乱序和断线注入；
- 现场联调验收脚本与回滚预案。
