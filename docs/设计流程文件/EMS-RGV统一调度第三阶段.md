# EMS/RGV 统一调度第三阶段设计

## 1. 阶段目标

第三阶段将第二阶段的内存执行能力升级为可恢复、可审计、可接入真实设备的执行平台，重点解决：

- WCS 重启后任务、车辆、路权和命令状态不丢失；
- 调度状态与 EMS/RGV 控制器实际状态能够对账；
- 逻辑命令与具体 PLC/厂商协议解耦；
- 命令下发具备持久化、确认、失败和重试状态；
- 恢复过程默认安全暂停，不自动驱动车辆运动。

## 2. 核心组件

### 2.1 ITransportStateStore

统一保存：

- TransportVehicleSnapshot
- TransportExecutionSnapshot
- RouteReservation
- TransportCommandRecord

Core 中提供 InMemoryTransportStateStore 供单元测试和模拟运行使用。生产环境由 Infrastructure 提供 SQL Server/SqlSugar 实现，并覆盖默认注册。

### 2.2 ITransportVehicleDriver

统一设备驱动接口：

- ReadStateAsync：读取车辆在线、位置、运行状态和活动命令；
- SendCommandAsync：发送移动、装载、卸载和停止命令。

驱动按 TransportVehicleKind 注册，TransportDriverResolver 负责解析 EMS 或 RGV 驱动。

### 2.3 TransportCommandDispatcher

命令处理顺序：

1. Pending 状态先写入存储；
2. 更新为 Sent；
3. 调用设备驱动；
4. 根据结果写入 Acknowledged、Completed、Failed 或 TimedOut；
5. 失败时按 MaxRetries 重试。

该顺序保证系统崩溃后仍能判断命令是否已创建、是否已发送以及是否得到设备确认。

### 2.4 TransportRecoveryCoordinator

恢复流程：

1. 加载持久化运行快照；
2. 忽略 Completed/Cancelled 终态任务；
3. 根据车辆类型解析设备驱动；
4. 读取车辆实际状态；
5. 校验在线状态和当前位置；
6. 状态一致时标记为 RestoredPaused；
7. 离线、位置不一致或车辆信息缺失时要求人工确认。

第三阶段禁止恢复后自动继续运行。

## 3. API

新增：

- GET /api/transport/runtime-snapshot
- POST /api/transport/recover
- POST /api/transport/commands/dispatch

现有第二阶段 API 保持兼容。

## 4. 安全原则

- 数据库状态不能直接覆盖设备实际状态；
- 位置不一致不得自动重建路权并继续运行；
- Load/Unload 命令必须依赖 CommandId 保证幂等；
- 现场驱动不得绕过 TransportCommandDispatcher 直接下发；
- 恢复完成仅表示状态可识别，不表示允许车辆运动。

## 5. 后续工作

- Infrastructure 实现 SqlSugarTransportStateStore；
- EMS PLC 和 RGV PLC 真实驱动；
- BackgroundService 自动保存运行态与派发 Outbox；
- Desktop 增加命令记录、恢复报告和人工确认页面；
- 第四阶段进入交叉口冲突、会车和死锁处理。
