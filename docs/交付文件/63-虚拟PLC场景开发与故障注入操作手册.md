# 63 虚拟 PLC 场景开发与故障注入操作手册

## 1. 阶段状态

S2 功能 Head `2662a88fda8bc460cf0242a6269eeb895e81d905` 已完成首轮仓库自动化验收：两条 S2 专项成功，S2 Full Regression 31/31 exact-head 成功。当前正在对证据回填后的 Evidence Head 执行同等二次复验。

该结论不代表 HIL、真实 PLC 网络协议、现场点位、电气联锁、机械安全或正式投产已验收。

## 2. 使用前提

必须同时满足：

```json
{
  "Simulator": { "Enabled": true },
  "SimulationGovernance": { "Enabled": true }
}
```

环境名称只能是 `Simulation` 或 `SimulationLoadTest`。Production 不允许启动模拟器，其他环境的 Simulation API 返回 404。

## 3. 推荐场景顺序

```text
登记受治理场景
→ 创建运行并保持 Paused
→ 定义 DB 块
→ 写入初始信号
→ 注入故障
→ 执行读取/写入
→ 断言结果
→ 清除故障
→ 断言恢复
→ Checkpoint / Replay
```

## 4. DB 块操作

### 4.1 定义块

```json
{
  "Id": "define-plc1-db1",
  "AtMilliseconds": 0,
  "Order": 0,
  "Kind": "plc.block.define",
  "Target": "PLC1.DB1",
  "Payload": {
    "Size": 16,
    "InitialBase64": "AQIDBA=="
  }
}
```

`InitialBase64` 可省略。初始字节少于 Size 时补零，超过 Size 时拒绝。Target 必须符合 `PLC_NAME.DB<number>`。

### 4.2 写入

```json
{
  "Id": "write-speed",
  "AtMilliseconds": 100,
  "Order": 0,
  "Kind": "plc.block.write",
  "Target": "PLC1.DB1",
  "Payload": {
    "Offset": 4,
    "DataBase64": "B9A=",
    "ResultStateKey": "plc.write.speed"
  }
}
```

写入结果包含 Success、TimedOut、ErrorCode、ErrorMessage、Sequence 等字段。失败写入不改变 DB。

### 4.3 读取

```json
{
  "Id": "read-speed",
  "AtMilliseconds": 200,
  "Order": 0,
  "Kind": "plc.block.read",
  "Target": "PLC1.DB1",
  "Payload": {
    "Offset": 4,
    "Count": 2,
    "ResultStateKey": "plc.read.speed"
  }
}
```

读取结果的 Data 在 JSON 中表现为 Base64。单次场景传输受 `MaximumScenarioTransferBytes` 限制。

## 5. 连接切换

```json
{
  "Id": "disconnect-plc1",
  "AtMilliseconds": 300,
  "Order": 0,
  "Kind": "plc.connection.set",
  "Target": "PLC1",
  "Payload": { "Connected": false }
}
```

连接状态断言：

```json
{
  "Id": "plc1-is-disconnected",
  "AtMilliseconds": 301,
  "Order": 0,
  "Kind": "plc.connected",
  "Target": "PLC1",
  "Expected": false
}
```

## 6. 故障注入

统一动作格式：

```json
{
  "Id": "apply-fault",
  "AtMilliseconds": 1000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "fault-01",
    "Kind": "Timeout",
    "StartMilliseconds": 1000,
    "EndMilliseconds": 3000,
    "Offset": 0,
    "Length": 1
  }
}
```

支持的故障类型：

| Kind | Target | 行为 |
|---|---|---|
| `Disconnect` | PLC 或 DB | 返回 Disconnected，不执行真实网络操作 |
| `Timeout` | PLC 或 DB | 返回 TimedOut，不使用真实等待 |
| `ReadFailure` | PLC 或 DB | 读取返回失败 |
| `WriteFailure` | PLC 或 DB | 写入返回失败且不改变 DB |
| `Stuck` | DB | 读取返回故障建立时冻结值，底层 DB 仍可写 |
| `BitFlip` | DB | 在指定字节和 BitIndex 上翻转读取结果 |
| `Jitter` | DB | 按 Seed/Sequence/FaultId/字节位置产生确定性偏移 |
| `OutOfRange` | DB | 使用 ReplacementBase64 替换指定读取范围 |

示例 Jitter：

```json
{
  "Id": "apply-jitter",
  "AtMilliseconds": 4000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "jitter-speed",
    "Kind": "Jitter",
    "StartMilliseconds": 4000,
    "EndMilliseconds": 8000,
    "Offset": 4,
    "Length": 2,
    "JitterMinimum": -3,
    "JitterMaximum": 3
  }
}
```

示例 OutOfRange：

```json
{
  "Id": "apply-out-of-range",
  "AtMilliseconds": 9000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "invalid-speed",
    "Kind": "OutOfRange",
    "StartMilliseconds": 9000,
    "EndMilliseconds": 11000,
    "Offset": 4,
    "Length": 2,
    "ReplacementBase64": "//8="
  }
}
```

## 7. 清除故障

```json
{
  "Id": "clear-invalid-speed",
  "AtMilliseconds": 11001,
  "Order": 0,
  "Kind": "plc.fault.clear",
  "Target": "invalid-speed",
  "Payload": {}
}
```

故障也会在 EndMilliseconds 后自动失效。

## 8. 断言

基础 DB 字节断言：

```json
{
  "Id": "speed-bytes-match",
  "AtMilliseconds": 12000,
  "Order": 0,
  "Kind": "plc.block.equals",
  "Target": "PLC1.DB1",
  "Expected": {
    "Offset": 4,
    "DataBase64": "B9A="
  }
}
```

故障状态断言：

```json
{
  "Id": "fault-cleared",
  "AtMilliseconds": 12001,
  "Order": 0,
  "Kind": "plc.fault.active",
  "Target": "invalid-speed",
  "Expected": false
}
```

`plc.block.equals` 检查基础 DB 字节，不应用读取故障。故障后的读取结果应通过 `ResultStateKey` 和现有状态断言检查。

## 9. Checkpoint 与 Replay

虚拟 DB、连接状态、Fault、OperationSequence 和环形 Audit 都存放在当前运行的 `SimulationStateStore`，因此会自动进入：

- Checkpoint StateJson；
- Checkpoint SHA-256；
- Replay EvidenceHash；
- FinalStateHash。

相同场景、Manifest、Seed 和输入必须获得相同结果。

## 10. 只读检查 API

```text
GET /api/simulation/virtual-plc/runs/{runId}/status
GET /api/simulation/virtual-plc/runs/{runId}/blocks
GET /api/simulation/virtual-plc/runs/{runId}/blocks/PLC1/db/1
GET /api/simulation/virtual-plc/runs/{runId}/faults
GET /api/simulation/virtual-plc/runs/{runId}/audit?take=100
```

检查接口通过当前运行 Checkpoint 解码状态，不修改场景。没有 Host 直接写 DB 或直接注入 Fault 的 API。

## 11. 常见错误

| 错误 | 处理 |
|---|---|
| block key 格式错误 | 使用 `PLC_NAME.DB<number>` |
| Base64 非法 | 重新编码原始字节 |
| 读写越界 | 检查 Offset + Count/Length |
| 重复 Block/Fault Id | 使用唯一标识 |
| 故障范围过大 | 不超过 `MaximumFaultPayloadBytes` |
| ResultStateKey 写入失败 | 缩小 Count，保持在 `MaximumScenarioTransferBytes` 内 |
| 非批准环境 404 | 切换到 Simulation 或 SimulationLoadTest |
| Production 启动失败 | Production 禁止 `Simulator.Enabled=true` |

## 12. 回退与安全

回退时先关闭 `SimulationGovernance.Enabled`，再关闭 `Simulator.Enabled`。S2 只用于进程内确定性仿真，不是 S7、OPC UA 或 Modbus 网络服务端，不得连接真实控制链。