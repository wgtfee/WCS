# 63 虚拟 PLC 场景开发与故障注入操作手册

## 1. 使用前提

必须同时满足：

```json
{
  "Simulator": { "Enabled": true },
  "SimulationGovernance": { "Enabled": true }
}
```

环境名称必须是：

```text
Simulation
SimulationLoadTest
```

Production 不允许启动模拟器，其他环境的 Simulation API 返回 404。

## 2. 推荐场景顺序

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

## 3. 定义 DB 块

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

`InitialBase64` 可省略。初始字节少于 Size 时，其余部分补零；超过 Size 时拒绝。

## 4. 写入 DB

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

写入结果包含 Success、TimedOut、ErrorCode、Sequence 等字段。失败写入不改变 DB。

## 5. 读取 DB

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

读取结果的 Data 在 JSON 中表现为 Base64。

## 6. 连接切换

```json
{
  "Id": "disconnect-plc1",
  "AtMilliseconds": 300,
  "Order": 0,
  "Kind": "plc.connection.set",
  "Target": "PLC1",
  "Payload": {
    "Connected": false
  }
}
```

连接状态可通过以下断言检查：

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

## 7. 注入故障

### 7.1 Disconnect

```json
{
  "Id": "apply-disconnect",
  "AtMilliseconds": 1000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1",
  "Payload": {
    "Id": "disconnect-01",
    "Kind": "Disconnect",
    "StartMilliseconds": 1000,
    "EndMilliseconds": 3000,
    "Offset": 0,
    "Length": 1
  }
}
```

### 7.2 Timeout

```json
{
  "Id": "apply-timeout",
  "AtMilliseconds": 4000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "timeout-01",
    "Kind": "Timeout",
    "StartMilliseconds": 4000,
    "EndMilliseconds": 5000,
    "Offset": 0,
    "Length": 1
  }
}
```

Timeout 是结构化结果，不会真的等待指定毫秒数。

### 7.3 Stuck

```json
{
  "Id": "apply-stuck",
  "AtMilliseconds": 6000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "stuck-speed",
    "Kind": "Stuck",
    "StartMilliseconds": 6000,
    "EndMilliseconds": 9000,
    "Offset": 4,
    "Length": 2
  }
}
```

Stuck 保存故障建立时的指定范围。底层 DB 后续仍可写入，但故障时间窗内读取返回冻结值。

### 7.4 BitFlip

```json
{
  "Id": "apply-bit-flip",
  "AtMilliseconds": 10000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "flip-ready",
    "Kind": "BitFlip",
    "StartMilliseconds": 10000,
    "EndMilliseconds": 12000,
    "Offset": 0,
    "Length": 1,
    "BitIndex": 0
  }
}
```

### 7.5 Jitter

```json
{
  "Id": "apply-jitter",
  "AtMilliseconds": 13000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "jitter-speed",
    "Kind": "Jitter",
    "StartMilliseconds": 13000,
    "EndMilliseconds": 20000,
    "Offset": 4,
    "Length": 2,
    "JitterMinimum": -3,
    "JitterMaximum": 3
  }
}
```

相同 Seed、Sequence 和字节位置产生相同抖动。

### 7.6 OutOfRange

```json
{
  "Id": "apply-out-of-range",
  "AtMilliseconds": 21000,
  "Order": 0,
  "Kind": "plc.fault.apply",
  "Target": "PLC1.DB1",
  "Payload": {
    "Id": "invalid-speed",
    "Kind": "OutOfRange",
    "StartMilliseconds": 21000,
    "EndMilliseconds": 23000,
    "Offset": 4,
    "Length": 2,
    "ReplacementBase64": "//8="
  }
}
```

## 8. 清除故障

```json
{
  "Id": "clear-invalid-speed",
  "AtMilliseconds": 23001,
  "Order": 0,
  "Kind": "plc.fault.clear",
  "Target": "invalid-speed",
  "Payload": {}
}
```

## 9. 断言 DB 字节

```json
{
  "Id": "speed-bytes-match",
  "AtMilliseconds": 24000,
  "Order": 0,
  "Kind": "plc.block.equals",
  "Target": "PLC1.DB1",
  "Expected": {
    "Offset": 4,
    "DataBase64": "B9A="
  }
}
```

该断言检查基础 DB 字节，不应用读取故障。故障后的读取结果应通过 `ResultStateKey` 和现有状态断言检查。

## 10. 断言故障状态

```json
{
  "Id": "fault-cleared",
  "AtMilliseconds": 24001,
  "Order": 0,
  "Kind": "plc.fault.active",
  "Target": "invalid-speed",
  "Expected": false
}
```

## 11. 只读检查 API

```text
GET /api/simulation/virtual-plc/runs/{runId}/status
GET /api/simulation/virtual-plc/runs/{runId}/blocks
GET /api/simulation/virtual-plc/runs/{runId}/blocks/PLC1/db/1
GET /api/simulation/virtual-plc/runs/{runId}/faults
GET /api/simulation/virtual-plc/runs/{runId}/audit?take=100
```

检查接口通过当前运行 Checkpoint 解码状态，不修改场景。终态运行的详细结果应使用运行 Evidence 和最终 State Hash。

## 12. 常见错误

| 错误 | 处理 |
|---|---|
| block key 格式错误 | 使用 `PLC_NAME.DB<number>` |
| Base64 非法 | 重新编码原始字节 |
| 读写越界 | 检查 Offset + Count/Length |
| 重复 Block/Fault Id | 使用唯一标识 |
| 故障范围过大 | 不超过 MaximumFaultPayloadBytes |
| ResultStateKey 写入失败 | 缩小 Count，保持在 MaximumScenarioTransferBytes 内 |
| 非批准环境 404 | 切换到 Simulation 或 SimulationLoadTest |
| Production 启动失败 | Production 禁止 Simulator.Enabled=true |
