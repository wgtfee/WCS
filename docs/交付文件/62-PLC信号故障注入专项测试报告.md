# 62 PLC 信号故障注入专项测试报告

## 1. 阶段信息

- 阶段：WCS Simulation & Verification v1.0 — S2
- PR：#35
- 状态：专项测试执行中
- 功能 Head：待首轮冻结
- Evidence Head：待首轮证据回填后生成

本报告只记录仓库自动化验证，不代表 HIL、机械安全或现场验收。

## 2. 专项工作流

| 工作流 | 目的 | 首轮 | Evidence Head |
|---|---|---|---|
| WCS Simulation Virtual PLC | 编译、块内存、Checkpoint、Replay、Host 只读检查、容量边界 | 待回填 | 待回填 |
| WCS Simulation PLC Fault Injection | 8 类故障、故障时间窗、确定性、控制链零耦合 | 待回填 | 待回填 |
| WCS Simulation S2 Full Regression | 25 项历史 + S0 两项 + S1 两项 + S2 两项，共 31 项 | 待建立 | 待回填 |

## 3. 测试矩阵

### 3.1 DB 块

- 定义固定长度 DB；
- 初始字节不足时零填充；
- 重复定义拒绝；
- 块数量上限；
- 块字节上限；
- 越界读写拒绝；
- 分块存储恢复；
- SHA-256 一致。

### 3.2 连接与操作结果

- 显式连接/断开；
- Disconnect 故障；
- Timeout 返回结果而非真实等待；
- ReadFailure；
- WriteFailure；
- 健康时间窗恢复；
- 失败写入不改变块字节。

### 3.3 信号故障

- Stuck 冻结值；
- BitFlip；
- Jitter；
- OutOfRange；
- 只修改读取结果、不污染基础 DB；
- 同一 Seed 与 Sequence 结果一致；
- 故障结束或 Clear 后恢复正常。

### 3.4 Checkpoint 与 Replay

- DB 块进入 StateJson；
- Fault、连接状态、Sequence、Audit 进入 StateJson；
- 中断恢复结果等于连续执行；
- 两次 Replay 的 EvidenceHash 和 FinalStateHash 相同。

### 3.5 安全边界

静态门禁必须确认虚拟 PLC 目录不引用：

```text
IPlcClient
IPlcConnection
S7RealClient
Snap7
Socket
PlcWriter
CommandBus
DispatchEngine
ResourceLockManager
RouteLock
```

并确认不存在：

```text
Random.Shared
Task.Delay
Thread.Sleep
```

## 4. 首轮 exact-head 结果

待 S2 功能 Head 冻结并完成 31/31 后回填：

```text
Head = pending
workflowCount = 31
allSuccess = pending
```

## 5. Evidence Head 结果

待 61～63、配置手册与总索引回填后，再对新的 exact Head 运行相同 31 项矩阵。

## 6. 验收规则

S2 仅在以下条件全部满足后可合入 `develop`：

1. 两条 S2 专项成功；
2. 首轮 31/31 exact-head 成功；
3. Artifact 中所有 child `headSha` 等于首轮 Head；
4. 证据回填产生 Evidence Head；
5. Evidence Head 两条专项再次成功；
6. Evidence Head 31/31 再次成功；
7. PR Head 未漂移；
8. Squash merge 使用 `expected_head_sha`。

## 7. 未覆盖范围

- HIL；
- 真实 S7 网络协议兼容；
- PLC 扫描周期硬实时精度；
- 现场点位与电气安全；
- RGV/EMS 车辆、区段、路权和交通死锁。
