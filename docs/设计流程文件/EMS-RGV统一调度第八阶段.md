# EMS / RGV 统一调度第八阶段

## 1. 阶段目标

第八阶段在第七阶段真实 PLC 驱动、状态同步和重启对账基础上，补齐现场设备接入所需的联调闭环：

1. EMS、RGV 点位模板管理；
2. JSON、CSV、XLSX 点位表导入与校验；
3. PLC 在线探测和单点读取；
4. 受审批保护的单点写入；
5. 批量通信耗时和错误跟踪；
6. 故障码字典与 AlarmCenter 联动；
7. WCS/PLC 状态冲突持久化和人工处置；
8. 断线恢复后的命令补偿评估；
9. 仅允许 Stop 命令进行安全补偿；
10. Desktop 现场联调工作台；
11. 联调验收报告导出。

本阶段仍不猜测现场 DB 块、偏移量、状态码和命令码。所有现场点位均由点位表、模板或配置输入。

---

## 2. 总体架构

```text
点位文件 / 点位模板
        ↓
TransportPointTableImporter
        ↓
校验结果（不直接写入）
        ↓
审批：ChangeConfiguration
        ↓
ITransportPlcSignalMapService
        ↓
Wcs_TransportPlcSignalMap

PLC / 模拟标签访问器
        ↓
TransportObservedPlcAccessor
        ├── 通信耗时
        ├── 成功/失败
        ├── 标签集合
        └── 车辆/驱动关联
        ↓
TransportPlcDriverChannel
        ↓
车辆状态、命令确认、故障码

故障码
        ↓
TransportFaultAlarmHostedService
        ↓
TransportFaultCatalogService
        ↓
AlarmCenter

重启对账
        ↓
TransportRecoveryConflictService
        ↓
Wcs_TransportCommissioning
        ↓
审批后人工处置
```

---

## 3. 点位模板

### 3.1 模板模型

`TransportSignalTemplate` 保存：

```text
TemplateId
Name
Kind
Protocol
MapPrototype
Version
UpdatedBy
UpdatedAtUtc
```

`MapPrototype` 使用第七阶段的 `TransportPlcSignalMap`，因此模板能够覆盖：

- 状态读取标签；
- 命令写入标签；
- 命令确认标签；
- 节点代码映射；
- 状态代码映射；
- 命令代码映射；
- 轮询和心跳参数；
- 模拟或 PLC 标签模式。

模板不绑定具体车辆。应用模板时才填写：

```text
VehicleId
DriverId
ExpectedMapVersion
```

### 3.2 版本控制

模板和点位映射均采用乐观版本控制：

```text
客户端 ExpectedVersion
        ↓
数据库当前 Version
        ↓
一致：Version + 1
不一致：返回 VersionConflict
```

模板保存、模板应用和批量点位应用都属于配置变更，必须使用：

```text
TransportGovernedOperationType.ChangeConfiguration
```

---

## 4. 点位表导入

### 4.1 支持格式

```text
.json
.csv
.xlsx
```

CSV 可由 Excel 另存为 UTF-8 CSV。

XLSX 使用 .NET 内置 ZIP 和 OpenXML XML 读取，不引入 Office COM，不要求安装 Excel。当前读取：

```text
xl/worksheets/sheet1.xml
```

支持：

- inline string；
- shared string；
- 数字和布尔值；
- 第一工作表；
- 普通单元格。

不执行：

- Excel 公式；
- VBA 宏；
- 外部链接；
- 数据连接；
- 嵌入对象。

因此点位表导入不会执行用户文件中的程序代码。

### 4.2 表头

主要表头包括：

```text
VehicleId
DriverId
Kind
Mode
Enabled
PollIntervalMs
HeartbeatTimeoutMs
HeartbeatTag
DeviceOnlineTag
CurrentNodeTag
OperatingStateTag
BatteryPercentTag
FaultCodeTag
FaultMessageTag
StateSequenceTag
ActiveCommandIdTag
LoadPresentTag
CommandIdTag
CommandSequenceTag
CommandCodeTag
TargetNodeTag
CommandRequestTag
AcknowledgedCommandIdTag
AcknowledgedSequenceTag
CommandAcceptedTag
CommandCompletedTag
CommandErrorTag
NodeCodeMapJson
TargetNodeCodeMapJson
OperatingStateMapJson
CommandCodeMapJson
ExpectedVersion
```

### 4.3 校验规则

导入只解析和校验，不直接应用。检查内容包括：

- 文件非空且不超过 10MB；
- VehicleId、DriverId 非空；
- 同一文件 VehicleId 不重复；
- 轮询周期和心跳超时大于 0；
- PLC 模式必填标签完整；
- 节点和代码映射为有效 JSON；
- 枚举名称或数值有效；
- 每一条错误包含行号、字段和说明。

推荐流程：

```text
上传文件
  ↓
ValidatePointTable
  ↓
现场工程师检查校验结果
  ↓
创建 ChangeConfiguration 审批
  ↓
独立审批人批准
  ↓
ApplyPointTable
```

批量应用前会再次检查所有车辆的当前版本，避免明显的并发覆盖。点位映射仍按车辆逐条保存，因此现场应先完成校验再执行批量应用；批量应用不是跨车辆数据库事务。

---

## 5. 在线探测和单点诊断

### 5.1 在线探测

车辆在线探测读取该车辆映射中的全部已配置标签，并返回：

```text
VehicleId
DriverId
Connected
Values
DurationMs
Error
ProbedAtUtc
```

用途：

- 检查 PLC 客户端是否可用；
- 检查标签名是否正确；
- 检查数据类型是否符合预期；
- 检查一次批量读取耗时；
- 核对心跳、位置、状态、电量和命令确认值。

在线探测是只读操作，不需要危险操作审批。

### 5.2 单点读取

单点读取通过已有批量访问器读取一个标签。它不会绕过真实 PLC 客户端，也不会直接使用协议实现。

### 5.3 单点写入

单点写入属于高风险现场操作，必须满足：

```text
权限：transport.driver.signal-write
操作类型：WritePlcSignal
审批目标：signal:{VehicleId}:{Tag}
申请人和审批人不同
审批号一次性使用
```

写入值支持：

- null；
- bool；
- 整数；
- 浮点数；
- string；
- 其他 JSON 值按原始 JSON 文本处理。

Desktop 页面不提供单点写入按钮，避免现场人员误触。写入入口只通过受治理的 Host API 暴露。

---

## 6. 通信跟踪

`TransportObservedPlcAccessor` 包装第七阶段 `HybridTransportPlcAccessor`，记录：

```text
ConnectionCheck
BatchRead
BatchWrite
SingleRead
SingleWrite
CommandCompensation
```

每条记录包含：

```text
TraceId
DriverId
VehicleId
Operation
Tags
Success
DurationMs
Error
OccurredAtUtc
```

通信跟踪采用进程内有界队列：

```text
最大 2000 条
```

原因：

- PLC 轮询可达到 200ms；
- 每次轮询写数据库会产生大量 IO；
- 联调记录主要用于近期诊断；
- 有界缓冲能够防止日志无限增长。

模板、故障字典和冲突处置需要跨重启保存，因此进入 SQL；高频通信明细不默认落库。

---

## 7. 故障码字典与报警联动

### 7.1 故障字典

`TransportFaultDefinition` 包括：

```text
Kind
FaultCode
AlarmCode
Level
Message
RecommendedAction
Enabled
Version
```

同一车辆类型和故障码只能存在一条定义。

### 7.2 报警映射

后台服务每 500ms 读取驱动诊断：

```text
FaultCode = 0
    → 恢复该车辆当前故障报警

FaultCode != 0
    → 查询故障字典
    → 注册 AlarmRule
    → RaiseAlarmAsync
```

实际报警代码带车辆前缀：

```text
TRANSPORT_{VehicleId}_{AlarmCode}
```

如果故障字典没有定义，则生成：

```text
TRANSPORT_{VehicleId}_FAULT_{Kind}_{FaultCode}
```

同一故障持续期间只触发一次，不会每 500ms 重置报警防抖。故障码变化时先恢复旧故障，再触发新故障。

---

## 8. 重启冲突处置

第七阶段只生成对账报告。第八阶段把非一致结果保存为 `TransportRecoveryConflictCase`：

```text
VehicleNotPersisted
DeviceOffline
PositionMismatch
ActiveCommandMismatch
RequiresManualConfirmation
Failed
```

冲突状态：

```text
Pending
Resolved
Cancelled
```

支持的人工处置：

### 8.1 AcceptDeviceState

现场核对设备实际位置后，采用最近驱动诊断快照更新：

- 车辆注册表；
- 持久化车辆快照。

不会：

- 写 PLC；
- 自动启动任务；
- 自动释放路权；
- 自动继续运动。

### 8.2 KeepPersistedState

确认数据库状态仍为事实来源，仅关闭本次冲突记录。

### 8.3 FailPersistedCommand

将指定历史命令标记为 Failed，避免后续恢复逻辑继续把它视为活动命令。

### 8.4 MarkFieldVerified

仅记录现场已经核验，保留当前状态，不执行设备动作。

所有冲突处置要求：

```text
权限：transport.recovery.resolve
操作类型：ResolveRecoveryConflict
审批目标：recovery:{CaseId}
必须填写处置原因
独立审批
```

---

## 9. 断线重连与命令补偿

系统检查以下命令状态：

```text
Pending
Sent
Acknowledged
TimedOut
```

补偿决策：

```text
车辆离线
    → WaitForReconnect

车辆在线 + Stop
    → SafeStopRetry

车辆在线 + Move/Load/Unload
    → RequiresManualConfirmation
```

只有 `Stop` 被视为可重复下发的安全命令。即使是 Stop，执行补偿仍要求：

```text
权限：transport.command.compensate
操作类型：RetryCommandCompensation
审批目标：compensate:{CommandId}
独立审批
```

Move、Load、Unload 的物理执行结果可能已经发生但确认丢失，因此禁止自动重发，避免：

- 重复移动；
- 重复装载；
- 重复卸载；
- 载荷状态与数据库进一步分叉。

---

## 10. 数据持久化

新增表：

```text
Wcs_TransportCommissioning
```

采用统一记录结构保存：

```text
SignalTemplate
FaultDefinition
RecoveryConflict
```

字段：

```text
StateKey
Category
RecordId
PayloadJson
UpdatedAtUtc
```

数据库 CodeFirst 表数量由 17 张增加为 18 张。

---

## 11. Host API

基础路径：

```text
/api/transport/commissioning
```

主要接口：

```text
POST /point-table/validate
POST /point-table/apply

GET  /templates
PUT  /templates/{templateId}
POST /templates/{templateId}/apply

GET  /vehicles/{vehicleId}/probe
GET  /vehicles/{vehicleId}/signals/read
POST /vehicles/{vehicleId}/signals/write

GET  /traces

GET  /faults
PUT  /faults/{definitionId}

GET  /conflicts
POST /conflicts/refresh
POST /conflicts/{caseId}/resolve

GET  /compensation
POST /compensation/{commandId}/retry-stop

GET  /report/export
```

报告导出为 JSON，包含：

- 点位映射；
- 驱动诊断；
- 故障码字典；
- 冲突记录；
- 补偿评估；
- 最近 500 条通信跟踪。

---

## 12. Desktop 现场联调工作台

菜单：

```text
现场联调工作台
```

页面显示：

- 点位模板数量；
- 启用故障定义数量；
- 待处置冲突数量；
- 需要人工确认的补偿数量；
- 点位模板列表；
- 故障码字典；
- 恢复冲突；
- 命令补偿决策；
- 通信跟踪；
- 车辆在线探测。

Desktop 页面只开放：

- 刷新；
- 刷新冲突；
- 在线探测。

以下高风险操作不在页面直接提供：

- 单点写 PLC；
- 批量应用点位；
- 冲突处置；
- 命令补偿；
- 运动命令。

这些操作必须通过审批后调用 Host API。

---

## 13. 现场接入前置条件

正式接入真实 PLC 前需要准备：

1. EMS、RGV 车辆清单；
2. PLC 标签或 DB 地址表；
3. 心跳变化规则；
4. 在线标志定义；
5. 节点编码表；
6. 运行状态码表；
7. 命令码表；
8. 确认和完成语义；
9. 故障码及处理建议；
10. Stop 命令是否由设备厂商确认幂等；
11. 位置编码器、读头或控制器位置来源；
12. 现场账号权限和审批人配置。

如果设备厂商不能确认 Stop 幂等，应将 Stop 补偿入口禁用，全部转为人工现场操作。

---

## 14. 第八阶段完成标准

- JSON、CSV、XLSX 点位表能够解析和校验；
- 点位模板具备版本控制；
- 单点读写使用统一 PLC 访问器；
- 单点写入必须审批；
- 通信耗时和错误可查询；
- 故障码能够触发和恢复 AlarmCenter 报警；
- 重启差异形成持久化冲突记录；
- 冲突处置不自动恢复运动；
- 只有 Stop 允许受审批的补偿重试；
- Move、Load、Unload 永远不自动补发；
- Desktop 工作台可以读取联调状态；
- Core、Host、Desktop 通过 Windows CI。
