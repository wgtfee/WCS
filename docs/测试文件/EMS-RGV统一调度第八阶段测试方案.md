# EMS / RGV 统一调度第八阶段测试方案

## 1. 测试目标

验证第八阶段新增的现场联调闭环满足：

- 点位文件只能解析和校验，不能绕过审批直接应用；
- JSON、CSV、XLSX 三种格式能够生成统一点位映射；
- 在线探测和单点读取不改变 PLC 状态；
- 单点写入必须独立审批；
- 通信跟踪有界且包含耗时与错误；
- 故障码与 AlarmCenter 正确联动；
- 重启冲突必须人工处置；
- 只有 Stop 允许补偿重试；
- Move、Load、Unload 禁止自动补发；
- Desktop 不提供危险操作快捷按钮。

---

## 2. Core 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportCommissioningTests.cs
src/Wcs.Core.Tests/TransportCommissioningGovernanceTests.cs
```

### 2.1 JSON 点位表

输入一辆 EMS，并配置节点、状态和命令代码 JSON。

预期：

- `Success=true`；
- 生成一条 `TransportPlcSignalMap`；
- 节点 20 映射为 N2；
- MoveToNode 映射为命令码 101。

### 2.2 CSV 重复车辆

CSV 中放入两行相同 VehicleId。

预期：

- `Success=false`；
- 错误包含行号；
- 字段为 VehicleId；
- 说明包含“重复”。

### 2.3 XLSX 第一工作表

构造最小 OpenXML XLSX：

```text
xl/worksheets/sheet1.xml
```

预期：

- 能读取 inline string；
- 正确识别表头；
- 生成 EMS-XLSX 映射；
- 不需要安装 Excel。

### 2.4 通信跟踪

通过 `TransportObservedPlcAccessor` 执行一次批量读取。

预期记录：

```text
Operation=BatchRead
Success=true
DriverId=DRV-01
VehicleId=EMS-01
DurationMs>=0
```

### 2.5 单点读写

在内存 PLC 访问器中写入 `manual.tag=88`，随后读取。

预期：

- 写入成功；
- 读取结果为 88；
- 分别生成 SingleWrite 和 SingleRead 跟踪记录。

### 2.6 模板版本冲突

步骤：

1. Version=0 保存模板；
2. 获得 Version=1；
3. 第二客户端仍使用 ExpectedVersion=0 保存；
4. 把第一版模板应用到 EMS-02。

预期：

- 第一次保存成功；
- 第二次返回 VersionConflict；
- 应用后的 VehicleId 为 EMS-02；
- DriverId 为 DRV-02。

### 2.7 采用设备状态

数据库车辆节点为 N1，驱动诊断节点为 N2，执行 `AcceptDeviceState`。

预期：

- 车辆注册表更新为 N2；
- 持久化车辆快照更新为 N2；
- 电量同步；
- 不调用 PLC 写入；
- 不自动继续任务。

### 2.8 补偿评估

创建：

```text
STOP-01 Status=Sent
MOVE-01 Status=Sent
```

车辆在线。

预期：

```text
STOP-01 → SafeStopRetry
MOVE-01 → RequiresManualConfirmation
```

### 2.9 新危险操作审批

分别测试：

```text
WritePlcSignal
ResolveRecoveryConflict
RetryCommandCompensation
```

预期：

- 申请后为 PendingApproval；
- 独立审批人批准后为 Approved；
- 正确权限的执行人才能 BeginExecution；
- 开始执行后为 Executing。

### 2.10 申请人禁止自批

同一账号同时具有写点和审批权限。

预期：

```text
ApproveAsync=false
错误包含“不同账号”
```

### 2.11 Stop 实际补偿

使用模拟驱动补偿一个 TimedOut Stop 命令。

预期：

- 命令状态变为 Completed；
- 使用原 CommandId 保持幂等关联；
- 写入补偿通信记录。

### 2.12 Move 补偿拒绝

对 TimedOut MoveToNode 调用补偿入口。

预期抛出：

```text
只有 Stop 命令允许自动补偿
```

---

## 3. Host API 测试

基础路径：

```text
/api/transport/commissioning
```

### 3.1 文件校验

```http
POST /api/transport/commissioning/point-table/validate
Content-Type: multipart/form-data
```

检查：

- 空文件返回 400；
- 超过 10MB 被拒绝；
- 不支持扩展名返回校验错误；
- JSON、CSV、XLSX 返回统一结果；
- 校验接口不修改数据库。

### 3.2 批量应用

先创建操作：

```text
OperationType=ChangeConfiguration
TargetId=point-table:bulk
```

然后调用：

```http
POST /api/transport/commissioning/point-table/apply
```

检查：

- 未审批返回 409；
- 申请人自批失败；
- 任意车辆版本冲突时不开始保存；
- 全部版本匹配后逐条保存；
- 审批号只能使用一次。

### 3.3 模板保存与应用

保存：

```http
PUT /api/transport/commissioning/templates/{templateId}
```

审批目标：

```text
template:{TemplateId}
```

应用：

```http
POST /api/transport/commissioning/templates/{templateId}/apply
```

审批目标：

```text
template-apply:{VehicleId}
```

检查模板版本冲突、车辆类型和 DriverId。

### 3.4 在线探测

```http
GET /api/transport/commissioning/vehicles/EMS-01/probe
```

检查：

- 读取全部已配置标签；
- 返回连接状态；
- 返回耗时；
- PLC 断线时返回 Error；
- 不写任何标签。

### 3.5 单点读取

```http
GET /api/transport/commissioning/vehicles/EMS-01/signals/read?tag=DB100.Node
```

检查返回标签和值。

### 3.6 单点写入

审批：

```text
OperationType=WritePlcSignal
TargetId=signal:EMS-01:DB200.Test
```

接口：

```http
POST /api/transport/commissioning/vehicles/EMS-01/signals/write
```

检查：

- 未审批不能写；
- 审批目标标签不一致不能写；
- bool、整数、浮点和字符串写入正确；
- 写入结果进入审计；
- 审批号不能二次使用。

### 3.7 故障字典

```http
GET /api/transport/commissioning/faults
PUT /api/transport/commissioning/faults/{definitionId}
```

审批目标：

```text
fault:{DefinitionId}
```

检查：

- 同 Kind + FaultCode 不允许重复；
- Version 冲突返回 409；
- AlarmCode 和 Message 不能为空；
- FaultCode 必须大于 0。

### 3.8 冲突处置

```http
GET  /api/transport/commissioning/conflicts
POST /api/transport/commissioning/conflicts/refresh
POST /api/transport/commissioning/conflicts/{caseId}/resolve
```

审批：

```text
OperationType=ResolveRecoveryConflict
TargetId=recovery:{CaseId}
```

检查：

- 必须填写 Reason；
- AcceptDeviceState 在设备离线时失败；
- FailPersistedCommand 必须存在命令；
- 已处置 Case 不能重复处置；
- 不产生 PLC 写入。

### 3.9 Stop 补偿

```http
GET  /api/transport/commissioning/compensation
POST /api/transport/commissioning/compensation/{commandId}/retry-stop
```

审批：

```text
OperationType=RetryCommandCompensation
TargetId=compensate:{CommandId}
```

检查：

- 车辆离线不能补偿；
- 映射停用不能补偿；
- 非 Stop 返回冲突；
- Stop 成功后更新命令状态；
- 生成 CommandCompensation 跟踪。

### 3.10 通信跟踪

```http
GET /api/transport/commissioning/traces?maxCount=500
```

检查驱动、车辆、操作、标签、耗时和错误字段。

### 3.11 报告导出

```http
GET /api/transport/commissioning/report/export
```

检查 JSON 包含：

```text
Maps
Diagnostics
Faults
Conflicts
Compensation
Traces
```

---

## 4. AlarmCenter 联动测试

### 4.1 已定义故障

故障字典：

```text
Kind=Ems
FaultCode=101
AlarmCode=MOTOR_OVERLOAD
Level=Error
```

PLC 上报 101。

预期报警代码：

```text
TRANSPORT_EMS-01_MOTOR_OVERLOAD
```

### 4.2 未定义故障

PLC 上报未知故障码 999。

预期：

```text
TRANSPORT_EMS-01_FAULT_Ems_999
```

### 4.3 持续故障

保持同一故障码 10 秒。

预期：

- 只触发一次 Raise 信号；
- 不每 500ms 重置防抖；
- 不产生报警风暴。

### 4.4 故障变化

```text
101 → 102
```

预期：

- 101 先进入恢复；
- 102 触发新报警；
- 车辆同时只有一个当前故障代码。

### 4.5 故障恢复

```text
FaultCode → 0
```

预期调用 `RecoverAlarmAsync`，经过恢复防抖后进入 Recovered。

---

## 5. 通信异常注入

### 5.1 PLC 断线

预期：

- Probe 返回连接失败；
- BatchRead 跟踪 Success=false；
- 驱动轮询继续运行；
- Host 不崩溃；
- 补偿决策为 WaitForReconnect。

### 5.2 心跳冻结

预期第七阶段驱动判离线，第八阶段不自动发送任何运动命令。

### 5.3 写入失败

预期：

- 单点写入结果 Success=false；
- 审批操作最终状态为 Failed；
- 审批号不可再次使用；
- 通信跟踪保留失败。

### 5.4 通信记录容量

连续生成超过 2000 条记录。

预期：

- 最旧记录被淘汰；
- 总数不超过 2000；
- 内存不会无限增长。

---

## 6. Desktop 验收

菜单：

```text
现场联调工作台
```

检查：

- 页面正常打开；
- 点位模板、故障码、冲突、补偿和通信记录正常加载；
- 在线探测显示标签数量和耗时；
- 刷新冲突不会改变设备；
- 页面没有单点写入按钮；
- 页面没有冲突直接处置按钮；
- 页面没有命令补偿按钮；
- 页面没有运动控制按钮。

---

## 7. 数据库验收

CodeFirst 应创建：

```text
Wcs_TransportCommissioning
```

验证 Category：

```text
0 SignalTemplate
1 FaultDefinition
2 RecoveryConflict
```

检查：

- 模板重启后可读取；
- 故障字典重启后可读取；
- Pending 冲突重启后仍存在；
- 通信跟踪默认不写 SQL；
- 数据库总表数日志更新为 18 张。

---

## 8. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点关注：

- `System.IO.Compression` 和 `System.Xml.Linq` 编译；
- init-only 属性的反射绑定；
- JsonStringEnumConverter 字典键；
- 泛型版本结果 nullable；
- SqlSugar OrderByType 和条件表达式；
- IFormFile multipart API；
- 新治理枚举 switch 完整性；
- HostedService DI 启动顺序；
- Avalonia UniformGrid、TabControl 和 DataGrid 绑定；
- CommunityToolkit 命令生成；
- Desktop API 接口实现完整。

---

## 9. 现场验收清单

正式接真实 PLC 时逐车执行：

1. 导入并校验点位表；
2. 审批后应用点位；
3. 在线探测全部标签；
4. 核对心跳持续变化；
5. 核对当前节点和状态码；
6. 核对电量和载荷；
7. 在设备厂商配合下测试命令握手；
8. 注入一个故障码并检查报警；
9. 清除故障并检查恢复；
10. 模拟断线并检查重连；
11. 模拟位置不一致并检查冲突；
12. 验证 Move 命令不会自动补发；
13. 验证 Stop 补偿必须独立审批；
14. 导出联调报告存档。
