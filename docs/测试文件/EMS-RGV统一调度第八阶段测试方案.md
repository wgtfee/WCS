# EMS / RGV 统一调度第八阶段测试方案

## 1. Core 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportCommissioningTests.cs
src/Wcs.Core.Tests/TransportCommissioningGovernanceTests.cs
```

### 1.1 点位表导入

验证：

- JSON 点位表生成有效映射；
- CSV 重复 VehicleId 被拒绝；
- XLSX 第一工作表可读取；
- 空文件、超大文件、未知格式被拒绝；
- PLC 模式必填标签缺失时返回行号和字段；
- 节点、状态和命令代码映射 JSON 正确转换。

### 1.2 通信跟踪

验证：

- 批量读写生成跟踪记录；
- 单点读写生成独立操作类型；
- 记录驱动号、车辆号、标签、耗时和错误；
- 环形缓冲超过容量时淘汰最旧记录。

### 1.3 点位模板

验证：

- 新模板 Version=0 保存为 Version=1；
- 旧版本保存返回 VersionConflict；
- 模板应用时替换 VehicleId 和 DriverId；
- 模板类型与车辆映射保持一致。

### 1.4 故障码字典

验证：

- 车辆类型 + FaultCode 正确解析定义；
- 停用定义不参与解析；
- 重复定义版本冲突；
- 报警级别、说明和建议处置正确保存。

### 1.5 恢复冲突

验证 PositionMismatch：

- AcceptDeviceState 更新车辆注册表；
- 更新持久化车辆快照；
- 不写 PLC；
- 不自动恢复执行任务；
- 冲突状态转为 Resolved；
- 保存处置人、原因和时间。

### 1.6 命令补偿

验证：

- Stop + 在线车辆判定为 SafeStopRetry；
- MoveToNode、Load、Unload 判定为 RequiresManualConfirmation；
- 离线车辆判定为 WaitForReconnect；
- 只有 Stop 可以执行 RetrySafeStopAsync；
- MoveToNode 重试必须抛出拒绝错误；
- Stop 重试通过 TransportCommandDispatcher 保存状态。

### 1.7 审批治理

对以下操作逐一验证：

```text
WritePlcSignal
ResolveRecoveryConflict
RetryCommandCompensation
```

检查：

- 缺少对应权限不能申请；
- 申请后进入 PendingApproval；
- 申请人不能审批自己的请求；
- 独立审批后进入 Approved；
- 目标不匹配不能执行；
- 审批号只能使用一次；
- 执行完成后写入审计记录。

## 2. Host API 测试

基础路径：

```text
/api/transport/commissioning
```

### 2.1 点位文件校验

```http
POST /point-tables/validate
```

分别上传 JSON、CSV、XLSX，检查 Maps 和 Issues。

### 2.2 点位文件应用

```http
POST /point-tables/apply
```

检查：

- 未审批返回 409；
- 文件存在校验错误时不保存任何映射；
- 审批目标不匹配返回 409；
- 正确审批后按每行 ExpectedVersion 保存；
- 任意一行版本冲突时返回明确结果。

### 2.3 单点读写

```http
GET  /vehicles/{vehicleId}/signals/read?tag=...
POST /vehicles/{vehicleId}/signals/write
```

检查读取无需写权限；写入必须使用目标：

```text
signal:{VehicleId}:{Tag}
```

### 2.4 冲突处置

```http
POST /conflicts/{caseId}/resolve
```

检查审批目标、处置原因、一次性执行及数据库状态。

### 2.5 Stop 补偿

```http
POST /compensation/{commandId}/retry-stop
```

检查只有 Stop 命令可执行，其他命令返回冲突。

### 2.6 验收报告

```http
GET /report
```

检查报告包含模板、故障字典、冲突、补偿和通信统计，且不泄漏审批凭据。

## 3. Desktop 验收

菜单：

```text
现场联调工作台
```

检查：

- 点位模板列表；
- 故障码字典列表；
- 待处置冲突列表；
- 命令补偿评估；
- 通信跟踪和耗时；
- 车辆在线探测；
- 页面不存在绕过审批的写点、运动和恢复按钮。

## 4. 现场异常注入

### 4.1 PLC 断线

预期在线探测失败，跟踪记录错误，Host 不崩溃，补偿决策为 WaitForReconnect。

### 4.2 心跳冻结

预期第七阶段驱动判离线，第八阶段不自动发送任何运动命令。

### 4.3 故障码切换

从故障码 A 切换到 B：旧报警恢复，新报警触发；B 持续存在时不得重复触发。

### 4.4 位置冲突

数据库为 N1、PLC 为 N2：生成冲突单，未经审批不得处置，处置后仍不得自动续跑。

### 4.5 命令超时

Stop 超时可以在审批后补偿；MoveToNode 超时只能人工确认，禁止自动重发。

## 5. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点关注：

- OpenXML ZIP/XML API；
- init 属性反射绑定；
- enum 字典 JSON 转换；
- SqlSugar 联调实体 CodeFirst；
- HostedService 注册顺序；
- Controller multipart 与 JSON 请求模型；
- Avalonia UniformGrid、DataGrid 和命令绑定；
- 新增治理枚举 switch 覆盖完整。
