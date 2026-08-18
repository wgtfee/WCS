# 61 虚拟 PLC 与 DB 块仿真设计说明

## 1. 文档状态

- 阶段：WCS Simulation & Verification v1.0 — S2
- 状态：实现与专项验证中
- 基线：S1 已合入 `develop`
- 适用环境：`Simulation`、`SimulationLoadTest`
- 不适用：Production、真实 PLC 联机、HIL 与现场验收

## 2. 设计目标

S2 在不连接任何真实 PLC 的前提下，为场景引擎提供可重复、可检查、可故障注入的 PLC 内存模型。实现必须复用 S1 的虚拟时钟、状态存储、Checkpoint、Replay 和 Evidence，不建立第二套时间轴或第二套持久化。

## 3. 架构位置

```text
Scenario DSL
  -> SimulationScenarioEngine
     -> VirtualPlcScenarioHandlers
        -> VirtualPlcRuntime
           -> SimulationStateStore
              -> S1 Checkpoint / Replay / State Hash
```

`VirtualPlcRuntime` 不依赖：

- `IPlcClient`
- `IPlcConnection`
- Snap7 / S7RealClient
- Socket
- `PlcWriter`
- CommandBus、DispatchEngine、ResourceLock、RouteLock

因此虚拟 PLC 无法把写入传递到真实设备。

## 4. DB 块模型

块标识固定为：

```text
PLC_NAME.DB<number>
```

示例：

```text
PLC1.DB1
EMS01.DB100
```

每个块包含：

- PLC 名称；
- DB 编号；
- 固定长度；
- 原始字节；
- SHA-256；
- 分块存储元数据。

原始字节按 1536 字节分块后以 Base64 写入 `SimulationStateStore`，从而满足单状态值 4096 字符上限，并让所有块数据自然进入 S1 Checkpoint。

## 5. 确定性规则

1. 不使用 `Random.Shared`。
2. 不使用 `Task.Delay` 或真实时间等待模拟超时。
3. 每次操作分配单调递增 Sequence。
4. Jitter 由会话随机状态、Sequence、FaultId 和字节偏移计算。
5. 同一场景、Seed、动作顺序和 Checkpoint 必须得到相同 DB 内容、Evidence 与 State Hash。
6. 不同运行会话使用各自的 `SimulationStateStore`，相互隔离。

## 6. 读写语义

### 6.1 读取

读取返回结构化结果：

- `Success`
- `TimedOut`
- `ErrorCode`
- `ErrorMessage`
- `Offset`
- `Count`
- `Data`
- `AppliedFaultIds`
- `Sequence`

超时不会阻塞线程，而是返回 `TimedOut=true` 和 `ErrorCode=Timeout`。

### 6.2 写入

写入只修改场景状态中的虚拟 DB 字节。成功写入记录修改前后 SHA-256；断连、超时或 WriteFailure 时不修改块数据。

## 7. 故障模型

| 故障 | 作用 |
|---|---|
| Disconnect | PLC 或目标块在指定虚拟时间窗内不可连接 |
| Timeout | 读写返回确定性超时结果 |
| ReadFailure | 读取失败且无数据 |
| WriteFailure | 写入失败且不改变 DB |
| Stuck | 读取指定范围时返回故障建立时冻结值 |
| BitFlip | 读取结果指定 bit 翻转，不修改底层 DB |
| Jitter | 读取结果按确定性增量抖动，不修改底层 DB |
| OutOfRange | 读取范围替换为指定越界/非法字节 |

故障包含：

- Id；
- Kind；
- Target；
- StartMilliseconds；
- EndMilliseconds；
- Offset；
- Length；
- Kind 专用参数；
- Enabled 状态。

## 8. DSL 动作与断言

动作：

```text
plc.block.define
plc.block.write
plc.block.read
plc.connection.set
plc.fault.apply
plc.fault.clear
```

断言：

```text
plc.block.equals
plc.connected
plc.fault.active
```

`plc.block.read` 和 `plc.block.write` 可将结构化结果写入指定 `ResultStateKey`，供现有 `state.equals` 等断言继续使用。

## 9. 审计

虚拟 PLC 审计为有界环形记录，字段包括：

- Sequence；
- 虚拟时间；
- 操作类型；
- Target；
- 成功/超时/ErrorCode；
- Offset/Count；
- 修改前后 SHA-256；
- AppliedFaultIds。

审计同样位于 `SimulationStateStore`，Checkpoint 恢复后操作序列和审计顺序保持一致。

## 10. Host 检查接口

只读接口：

```text
GET /api/simulation/virtual-plc/runs/{runId}/status
GET /api/simulation/virtual-plc/runs/{runId}/blocks
GET /api/simulation/virtual-plc/runs/{runId}/blocks/{plcName}/db/{dbNumber}
GET /api/simulation/virtual-plc/runs/{runId}/faults
GET /api/simulation/virtual-plc/runs/{runId}/audit?take=100
```

接口复用 S0 双开关和环境白名单。非批准环境统一返回 404。为避免绕开 DSL 破坏 Replay，S2 不提供 Host 外部写块或直接注入故障接口。

## 11. 默认容量

`Simulation`：

- MaximumBlocks：128
- MaximumBlockBytes：65536
- MaximumOperationBytes：65536
- MaximumScenarioTransferBytes：1536
- MaximumFaults：1024
- MaximumFaultPayloadBytes：1536
- MaximumAuditRecords：1000

`SimulationLoadTest` 可提高块、故障和审计数量，但单次 DSL 传输仍保持 1536 字节，确保结果可写入现有有界状态值。

## 12. 阶段边界

S2 不包含：

- 真实 PLC 协议服务端；
- Snap7/OPC UA/Modbus 网络模拟器；
- HIL；
- 真实点位表导入；
- RGV、区段和交通模型；
- 现场安全验收。

这些能力分别属于后续 S3～S9。
