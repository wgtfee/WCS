# EMS / RGV 统一调度第七阶段测试方案

## 1. Core 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportPlcDriverTests.cs
```

### 1.1 命令写入和确认关联

验证：

- 命令序号正确写入；
- 命令码按配置映射；
- 目标节点转换为 PLC 节点码；
- 参数先写，请求位后写；
- PLC 返回相同确认序号后关联到正确 CommandId；
- 确认后请求位清零。

### 1.2 心跳停止

场景：

- 第一次读取心跳值为 1；
- 超过 HeartbeatTimeoutMs 后心跳仍为 1。

预期：

- DeviceOnline=false；
- OperatingState=Offline；
- 诊断记录连续读取状态。

### 1.3 可靠命令确认

验证：

- ReliableTransportVehicleDriver 等待对应序号；
- 错误序号不能完成命令；
- 正确序号且 Accepted=true 后返回成功；
- Completed=true 时命令记录可进入 Completed。

### 1.4 状态同步

验证 PLC 上报：

```text
NodeCode=20
StateCode=2
Battery=64
StateSequence=3
```

预期车辆注册表：

```text
CurrentNodeId=N2
State=Executing
BatteryPercent=64
IsOnline=true
```

### 1.5 重启位置不一致

数据库节点为 N1，PLC 节点为 N2。

预期：

```text
PositionMismatch
ManualConfirmationCount=1
```

系统不得自动写 PLC 或继续任务。

### 1.6 点位映射版本冲突

- 第一次按 Version=0 保存成功，得到 Version=1；
- 第二个客户端仍按 Version=0 保存。

预期：

- 返回 VersionConflict；
- 已应用映射保持第一版；
- 不覆盖点位表。

---

## 2. Host API 测试

### 2.1 查询映射

```http
GET /api/transport/drivers/maps
```

### 2.2 查询诊断

```http
GET /api/transport/drivers/diagnostics
```

检查：

- 心跳时间；
- 在线状态；
- 当前节点；
- 状态和电量；
- 故障码；
- 状态序号；
- 确认序号；
- 待确认命令；
- 最近错误。

### 2.3 保存映射

先创建并审批：

```text
OperationType=ChangeConfiguration
TargetId=plc-map:EMS-01
```

然后：

```http
PUT /api/transport/drivers/maps/EMS-01
```

检查：

- 未审批返回 409；
- 申请人自己审批失败；
- 审批目标不匹配失败；
- 正确审批后保存成功；
- 审批号不能重复使用。

### 2.4 手动命令

先创建并审批：

```text
OperationType=SendManualDriverCommand
TargetId=EMS-01
```

只先测试：

```text
Stop
```

检查：

- 未审批不能下发；
- 命令通过 TransportCommandDispatcher 持久化；
- 确认超时正确记录；
- 重复审批号不能执行第二次命令。

---

## 3. Desktop 验收

菜单：

```text
PLC 驱动诊断
```

检查：

- 点位映射表正常加载；
- 实时诊断表正常加载；
- “立即轮询”刷新状态；
- “安全对账”只生成报告；
- 页面不提供绕过审批的运动按钮；
- 离线、故障和人工确认数量正确。

---

## 4. 现场异常注入

### 4.1 PLC 断线

预期：

- 驱动诊断离线；
- 车辆退出派单池；
- Host 不崩溃；
- 恢复连接后状态重新同步。

### 4.2 心跳冻结

保持连接但冻结 HeartbeatTag。

预期仍判定离线，不能仅依赖 TCP 连接状态。

### 4.3 确认超时

PLC 不更新 AcknowledgedSequence。

预期：

- 命令超时；
- 不生成虚假成功；
- 调度器按既有重试策略处理。

### 4.4 重复确认

PLC 重复返回上一条确认序号。

预期：

- 新命令继续等待；
- 不把旧确认当作新确认。

### 4.5 位置跳变

PLC 从 N1 直接上报不在剩余路径中的节点。

预期：

- 执行引擎拒绝位置反馈；
- 不释放错误路段；
- 诊断和日志保留异常。

---

## 5. Windows CI

必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点关注：

- 新增字典类型 JSON 序列化；
- SqlSugar 新实体 CodeFirst；
- DI 中每种车辆类型只有一个驱动；
- 可选 IPlcClient 在模拟模式下不导致 DI 失败；
- Avalonia DataGrid 绑定；
- CommunityToolkit 命令生成；
- Controller 路由无冲突。
