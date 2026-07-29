# 62 PLC 信号故障注入专项测试报告

## 1. 阶段信息

- 阶段：WCS Simulation & Verification v1.0 — S2
- PR：#35
- 状态：首轮仓库验收完成，Evidence Head 二次复验待执行
- 功能 Head：`2662a88fda8bc460cf0242a6269eeb895e81d905`
- Evidence Head：由本次证据回填提交生成

本报告只记录仓库自动化验证，不代表 HIL、机械安全、真实 S7 网络兼容或现场验收。

## 2. 专项工作流首轮证据

| 工作流 | Run | 结论 | Artifact | Digest |
|---|---:|---|---|---|
| WCS Simulation Virtual PLC | #11 / `30455157970` | success | `wcs-simulation-virtual-plc-11` | `sha256:6b0538e8b09bd55866ec94f55aba76c9df63a02572b0293030d1293c65a92b07` |
| WCS Simulation PLC Fault Injection | #11 / `30455157903` | success | `wcs-simulation-plc-fault-injection-11` | `sha256:da66e93a8fd574f07caafbf5209d97464ebcc8e257527e6a3115893b1da50a97` |
| WCS Simulation S2 Full Regression | #4 / `30455157907` | success | `wcs-simulation-s2-full-regression-4` | `sha256:8b6eab54a2971175a9573743753228110ca0155ee7f575ec097ecb45bcc30cfd` |

## 3. 测试矩阵

### 3.1 DB 块

- 定义固定长度 DB；
- 初始字节不足时零填充；
- 重复定义拒绝；
- 块数量上限；
- 块字节上限；
- 越界读写拒绝；
- 1,536 字节分块存储与恢复；
- SHA-256 一致。

### 3.2 连接与操作结果

- 显式连接/断开；
- Disconnect 故障；
- Timeout 返回结构化结果而非真实等待；
- ReadFailure；
- WriteFailure；
- 健康时间窗恢复；
- 失败写入不改变块字节。

### 3.3 信号故障

- Stuck 冻结值；
- BitFlip；
- Jitter；
- OutOfRange；
- 故障只修改读取结果，不污染基础 DB；
- 同一 Seed、Sequence、FaultId 与字节位置结果一致；
- 故障结束或 Clear 后恢复正常。

### 3.4 Checkpoint 与 Replay

- DB 块进入 StateJson；
- Fault、连接状态、OperationSequence、Audit 进入 StateJson；
- 中断恢复结果等于连续执行；
- 两次 Replay 的 EvidenceHash 和 FinalStateHash 相同。

### 3.5 Host 与安全边界

- Production 和非批准环境返回 404；
- Host 只提供 status、blocks、block detail、faults、audit 只读检查；
- 不提供绕过 DSL 的直接写块或直接故障注入接口；
- 虚拟 PLC 目录不引用 `IPlcClient`、`IPlcConnection`、`S7RealClient`、`Snap7`、`Socket`、`PlcWriter`、`CommandBus`、`DispatchEngine`、`ResourceLockManager` 或 `RouteLock`；
- 不使用 `Random.Shared`、`Task.Delay` 或 `Thread.Sleep` 模拟故障。

## 4. 首轮 exact-head 结果

```text
Head = 2662a88fda8bc460cf0242a6269eeb895e81d905
workflowCount = 31
allSuccess = true
```

已下载并核实 Full Regression Artifact 中的 `evidence.json`：

- `workflowCount=31`；
- `allSuccess=true`；
- 31 条 child 全部 `status=completed`；
- 31 条 child 全部 `conclusion=success`；
- 31 条 child 的 `headSha` 全部等于功能 Head；
- PR Head 校验未漂移。

31 条累计矩阵包括 25 条历史工作流、S0 两条、S1 两条以及 S2 两条专项。One Hour Soak、Engine Soak、Forecast Host+SQL、Windows CI、E2E、Telemetry、ML、Health、Governance、Root Cause 和 Maintenance 均在同一 exact Head 成功。

## 5. Evidence Head 结果

本次文档、配置手册、总索引和 PR 描述回填将产生新的 Evidence Head。新的 Head 必须再次满足：

```text
Virtual PLC = success
PLC Fault Injection = success
S2 Full Regression workflowCount = 31
S2 Full Regression allSuccess = true
所有 child headSha = Evidence Head
```

第二轮结果完成前，PR #35 保持 Draft，不得合入 `develop`。

## 6. 验收规则

S2 仅在以下条件全部满足后可合入 `develop`：

1. 两条 S2 专项在功能 Head 成功；
2. 首轮 31/31 exact-head 成功；
3. Artifact 中所有 child `headSha` 等于首轮 Head；
4. 证据回填产生 Evidence Head；
5. Evidence Head 两条专项再次成功；
6. Evidence Head 31/31 再次成功；
7. PR Head 未漂移；
8. Squash merge 使用 `expected_head_sha`。

## 7. 未覆盖范围

- HIL；
- 真实 S7/OPC UA/Modbus 网络服务端兼容；
- PLC 扫描周期硬实时精度；
- 现场点位、电气联锁和机械安全；
- RGV/EMS 车辆、区段、路权和交通死锁；
- S3～S10 后续能力。