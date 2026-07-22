# EMS / RGV 统一调度第七阶段

## 1. 阶段目标

第七阶段把前六个阶段形成的调度内核接到真实 PLC 通信边界，完成：

1. EMS、RGV 车辆点位映射配置；
2. S7 标签批量读取和批量写入；
3. 命令序号、请求、确认、完成和错误握手；
4. 心跳变化检测和离线判定；
5. 节点码、状态码和命令码转换；
6. PLC 状态同步到车辆注册表和执行引擎；
7. WCS 重启后的设备状态安全对账；
8. 模拟通道和真实 PLC 通道自动切换；
9. Host 诊断 API 和 Desktop 驱动诊断页面；
10. 手动驱动命令继续受第六阶段审批控制。

本阶段不猜测任何现场 DB 块、偏移量和厂商状态码。现场点位全部通过配置输入。

---

## 2. 驱动分层

```text
TransportCommandDispatcher
        ↓
SwitchableTransportVehicleDriver
        ├── Simulation → SimulatorTransportVehicleDriver
        └── PlcTag     → ReliableTransportVehicleDriver
                              ↓
                     TransportPlcDriverChannel
                              ↓
                     ITransportPlcAccessor
                     ├── HybridTransportPlcAccessor
                     ├── PlcClientTransportPlcAccessor
                     └── InMemoryTransportPlcAccessor
                              ↓
                       IPlcClient 批量标签读写
```

`HybridTransportPlcAccessor` 在 Host 已注册 `IPlcClient` 时使用真实 PLC；没有真实客户端时自动回退到内存访问器。因此：

- Windows CI 不需要 PLC；
- 离线开发不需要修改业务代码；
- 现场启用真实 PLC 后使用同一套调度服务。

---

## 3. 点位映射

每辆车保存一条 `TransportPlcSignalMap`，主要字段包括：

### 3.1 状态读取

```text
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
```

### 3.2 命令写入

```text
CommandIdTag
CommandSequenceTag
CommandCodeTag
TargetNodeTag
CommandRequestTag
```

### 3.3 命令确认

```text
AcknowledgedCommandIdTag
AcknowledgedSequenceTag
CommandAcceptedTag
CommandCompletedTag
CommandErrorTag
```

### 3.4 代码映射

```text
NodeCodeMap             PLC 节点码 → WCS NodeId
TargetNodeCodeMap       WCS NodeId → PLC 节点码
OperatingStateMap       PLC 状态码 → 统一车辆状态
CommandCodeMap          统一命令类型 → PLC 命令码
```

点位映射独立存储在：

```text
Wcs_TransportPlcSignalMap
```

每辆车带 `Version`，保存时使用乐观并发检查，避免两名工程师同时修改点位表造成覆盖。

---

## 4. 命令握手

命令写入顺序固定为：

```text
CommandId / Sequence / CommandCode / TargetNode
                    ↓
             CommandRequest = true
```

PLC 返回：

```text
AcknowledgedSequence
CommandAccepted
CommandCompleted
CommandError
```

WCS 只有在确认序号与本次命令一致时才接受确认。确认完成后：

```text
CommandRequest = false
```

这样可以避免：

- PLC 读取到一半更新的命令参数；
- 上一条命令确认被错误用于下一条命令；
- 网络重试产生重复动作；
- 请求位长期保持导致 PLC 重复执行。

`ReliableTransportVehicleDriver` 继续负责：

- 每车串行下发；
- 命令序号递增；
- 重复确认幂等；
- 确认超时；
- 心跳超时。

---

## 5. 心跳和在线状态

本阶段不是简单读取一个在线布尔值，而是监控 `HeartbeatTag` 的值是否持续变化。

在线条件：

```text
PLC 访问器连接正常
AND DeviceOnlineTag = true（配置时）
AND HeartbeatTag 在 HeartbeatTimeoutMs 内发生过变化
```

心跳停止变化后：

- 驱动诊断转为离线；
- 车辆注册表转为 `Offline`；
- 不再接受新任务；
- 活动任务不自动换车，后续由故障策略或人工确认处理。

---

## 6. 状态同步

`TransportDriverPollingHostedService` 周期调用：

```text
ITransportDriverSynchronizationService.PollAllAsync
```

同步内容：

- 在线状态；
- 当前节点；
- 电量；
- 统一运行状态；
- 故障码；
- 状态序号。

当车辆存在活动执行任务时：

- 节点变化且状态序号递增：调用 `ApplyPositionFeedback`；
- 故障码非零：执行任务转为 `Faulted` 并生成停止命令；
- 重复或乱序状态序号：忽略；
- 空节点或离线状态：不推进路径。

---

## 7. 重启对账

启动时执行一次安全对账：

```text
数据库车辆快照
数据库活动命令
        ↕
PLC 当前节点
PLC 活动命令
PLC 在线状态
```

结果包括：

```text
InSync
VehicleNotPersisted
DeviceOffline
PositionMismatch
ActiveCommandMismatch
RequiresManualConfirmation
Failed
```

即使结果为 `InSync`，系统也只保持暂停等待确认，不自动恢复运动。

以下情况必须人工处理：

- 数据库没有车辆快照；
- PLC 离线；
- 数据库节点与 PLC 节点不同；
- 数据库活动命令与 PLC 活动命令不同。

---

## 8. 权限和手动联调

点位映射修改属于：

```text
ChangeConfiguration
```

手动驱动命令属于：

```text
SendManualDriverCommand
```

二者均使用第六阶段治理流程：

```text
申请
  ↓
独立账号审批
  ↓
一次性执行
  ↓
操作审计
```

Desktop 页面默认不开放手动命令按钮，只提供读取、立即轮询和安全对账。

---

## 9. Host API

基础路径：

```text
/api/transport/drivers
```

接口：

```text
GET    /maps
PUT    /maps/{vehicleId}
DELETE /maps/{vehicleId}
GET    /diagnostics
POST   /poll
POST   /reconcile
POST   /vehicles/{vehicleId}/manual-command
```

修改和删除点位映射需要 `ChangeConfiguration` 类型审批号。
手动命令需要 `SendManualDriverCommand` 类型审批号。

---

## 10. Desktop 页面

新增菜单：

```text
PLC 驱动诊断
```

页面展示：

- 点位映射和版本；
- 驱动连接和设备在线状态；
- 当前节点、车辆状态和电量；
- 故障码；
- 状态序号和确认序号；
- 待确认命令；
- 连续读取失败；
- 重启对账结果。

---

## 11. 现场接入步骤

1. 在运行配置中创建 DriverId；
2. 为每辆 EMS/RGV 建立点位映射；
3. 填写实际 PLC 标签名；
4. 填写节点码、状态码和命令码；
5. 在模拟模式验证握手；
6. 切换 `Mode=PlcTag`；
7. 先查看诊断页心跳和状态；
8. 执行安全对账；
9. 审批后测试 Stop；
10. 再测试 Move、Load 和 Unload。

禁止在没有确认急停、限位和设备本地安全逻辑的情况下直接测试运动命令。
