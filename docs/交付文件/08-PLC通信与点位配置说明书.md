# EMS/RGV PLC 通信与点位配置说明书

## 1. 目的

本文说明 EMS/RGV 统一调度与 PLC/控制器之间的通信模型、点位映射、命令相关性、心跳、可信位置、联调和故障处置要求。

## 2. 设计边界

WCS 负责：

- 业务任务号和命令序号；
- 点位映射和统一命令；
- 命令发送、确认、完成和超时；
- 车辆在线、可信节点和状态同步；
- 诊断、Trace 和恢复对账。

PLC/控制器负责：

- 安全回路；
- 电机和伺服控制；
- 运动轨迹与速度闭环；
- 限位、急停、防撞和设备联锁；
- 到位和命令执行结果。

## 3. 驱动抽象

```text
ITransportVehicleDriver
ITransportDriverChannel
ITransportPlcAccessor
ITransportPlcSignalMapRegistry
ITransportDriverDiagnosticsService
```

业务和执行状态机不得直接使用 PLC DB 地址或标签。

## 4. 驱动模式

### 4.1 Simulator

用于开发、自动化测试和离线演示，不连接现场 PLC。

### 4.2 PlcTag

通过配置的标签读取和写入真实 PLC。点位由 `TransportPlcSignalMap` 提供。

后续可通过新的 Channel 支持 S7 块读、OPC UA 或厂商 SDK，但必须保持统一 Driver 接口。

## 5. 点位映射字段

每台车辆至少配置：

| 字段 | 方向 | 用途 |
|---|---|---|
| HeartbeatTag | PLC→WCS | 心跳值或计数器 |
| CurrentNodeTag | PLC→WCS | 当前可信节点/站点 |
| OperatingStateTag | PLC→WCS | 车辆运行状态 |
| StateSequenceTag | PLC→WCS | 状态序号，防止旧状态覆盖 |
| CommandSequenceTag | WCS→PLC | 当前命令序号 |
| CommandCodeTag | WCS→PLC | 命令类型 |
| CommandRequestTag | WCS→PLC | 命令请求位 |
| AcknowledgedSequenceTag | PLC→WCS | 已接收命令序号 |
| CommandAcceptedTag | PLC→WCS | 命令接收状态 |
| CommandCompletedTag | PLC→WCS | 命令完成状态 |
| FaultCodeTag | PLC→WCS | 故障码，可选 |
| BatteryTag | PLC→WCS | 电量，可选 |
| OccupancyTag | PLC→WCS | 物理占用，可选但建议配置 |

实际标签名、DB、偏移、类型和字节序由现场点位表确定。

## 6. 命令握手

推荐时序：

```text
WCS 写 CommandCode/参数
→ WCS 写 CommandSequence
→ WCS 置 CommandRequest
→ PLC 校验当前状态和参数
→ PLC 写 AcknowledgedSequence
→ PLC 置 CommandAccepted
→ PLC 执行命令
→ PLC 置 CommandCompleted
→ WCS 校验序号并记录 Completed
→ 双方按协议清理请求/完成位
```

### 6.1 相关性

WCS 只接受：

```text
AcknowledgedSequence == 当前 CommandSequence
```

旧确认、乱序确认和其他任务确认不得完成当前命令。

### 6.2 幂等

PLC 收到相同命令序号时应返回已有执行结果，不重复启动运动。WCS 重启后读取当前命令和确认序号进行对账，不自动重新写运动命令。

## 7. 心跳和在线判定

支持心跳计数器或周期变化值。

车辆离线条件可包括：

- Accessor 断开；
- 心跳超过 `HeartbeatTimeoutMs` 未变化；
- 最近读取时间过期；
- PLC 报通信故障；
- 点位解析失败达到阈值。

离线后：

- 不参与新派单；
- 活动任务进入安全评估；
- 已确认物理占用继续保留；
- 生成报警和恢复冲突。

## 8. 可信位置

PLC 无连续坐标时，WCS 使用节点位置：

```text
最后确认节点
+ 当前已确认物理区段
+ 当前任务和命令
```

禁止：

- 根据运行时间推算并写成真实节点；
- 通信断线后假设车辆已经到达；
- 仅根据 WCS 已发送命令更新位置。

位置确认来源可为：

- 到站信号；
- RFID/二维码读头；
- 编码器区段；
- EMS 控制器位置回执；
- 人工恢复确认。

## 9. 数据类型

点位表应明确：

- Bool/Byte/Int16/UInt16/Int32/UInt32/String；
- 字节序；
- 字符串长度和编码；
- 节点编号映射；
- 状态枚举值；
- 故障码范围；
- 命令码范围；
- 读写权限。

不得依赖 C# 默认类型大小猜测 PLC 布局。

## 10. 点位表模板

| VehicleId | DriverId | Mode | SignalName | Address/Tag | DataType | Direction | Required | Description |
|---|---|---|---|---|---|---|---|---|
| EMS-01 | EMS-DRV-01 | PlcTag | Heartbeat | 待现场填写 | UInt32 | R | 是 | 心跳计数 |
| EMS-01 | EMS-DRV-01 | PlcTag | CurrentNode | 待现场填写 | String/Int | R | 是 | 当前可信节点 |
| EMS-01 | EMS-DRV-01 | PlcTag | CommandSequence | 待现场填写 | UInt32 | W | 是 | 命令序号 |
| EMS-01 | EMS-DRV-01 | PlcTag | CommandRequest | 待现场填写 | Bool | W | 是 | 请求位 |
| EMS-01 | EMS-DRV-01 | PlcTag | AckSequence | 待现场填写 | UInt32 | R | 是 | 确认序号 |

项目实施时应输出完整 XLSX，并通过导入器校验。

## 11. 点位导入

支持：

- JSON；
- CSV；
- XLSX 第一工作表。

导入校验至少包括：

- VehicleId 重复；
- DriverId 不存在；
- 车辆类型不一致；
- 必填标签为空；
- 数据类型不支持；
- 读写方向错误；
- 版本冲突。

## 12. 联调流程

1. 确认 PLC 程序版本；
2. 确认 WCS 点位表版本；
3. 导入但不立即写生产；
4. 执行校验；
5. 单点读取心跳、状态和节点；
6. 在设备安全条件下审批单点写；
7. 验证命令序号和回执；
8. 验证完成、拒绝和超时；
9. 验证断线和重连；
10. 保存通信 Trace 和签署记录。

## 13. 单点写安全

单点写必须：

- 认证；
- 具备 `transport.driver.signal-write` 权限；
- 使用独立审批 OperationId；
- 目标车辆和信号与审批一致；
- 记录旧值、新值、执行人和结果；
- 不允许批量任意地址写入。

## 14. 故障码

故障码字典应包含：

- Code；
- Name；
- Severity；
- Description；
- PossibleCause；
- OperatorAction；
- ResetCondition；
- 是否允许自动恢复。

PLC 故障恢复不等同于 WCS 任务恢复。

## 15. 通信 Trace

每条 Trace 建议包含：

- TraceId；
- VehicleId；
- DriverId；
- Operation；
- Tags/Addresses；
- Request/Response 摘要；
- DurationMs；
- Success；
- Error；
- OccurredAtUtc。

生产环境应限制载荷大小和保留时间，避免记录敏感或超大原始数据。

## 16. 恢复对账

重启后比较：

- PLC 当前命令序号；
- PLC 确认序号；
- PLC 当前任务；
- PLC 当前节点；
- SQL 活动命令；
- 内存执行任务；
- 物理占用。

任何不确定情况进入人工恢复，不自动续发 Move/Load/Unload。
