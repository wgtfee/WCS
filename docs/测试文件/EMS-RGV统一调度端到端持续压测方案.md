# EMS/RGV 统一调度端到端持续压测方案

## 1. 目标

该压测验证真实 Host 进程中的以下链路可以同时持续运行：

```text
模拟 PLC（3 PLC / 9 DB）
        ↓
StateCenter + EventBus
        ↓
SignalR 实时推送

HTTP API → 业务服务 → SQL Server
```

该入口用于回归和稳定性证据，不直接代表现场硬件容量。

## 2. 安全边界

- 仅连接 GitHub Runner 内的临时 SQL Server 2022；
- 强制 `Simulator:Enabled=true`；
- 不注册真实 S7、S7CommPlus、Modbus 或 OPC UA 连接；
- 不访问现场 PLC、现场数据库或生产 URL；
- 数据库和测试数据随 Runner 销毁；
- 只使用仓库内置仿真器和测试任务。

负载脚本默认拒绝所有非回环地址。只有经过批准的隔离测试环境才可显式设置 `ALLOW_REMOTE_TARGET=true`，生产地址禁止使用该开关。

## 3. 覆盖范围

| 层 | 验证内容 |
|---|---|
| Host | Release 构建、真实进程启动、运行后存活检查 |
| HTTP | 健康、概览、设备、任务、可观测性和数据库查询接口混合并发 |
| SQL Server | CodeFirst 建表、设备/任务状态写入、并发查询、最终行数证据 |
| 模拟 PLC | 3 个 PLC、9 个 DB 块按配置周期生成和解析数据 |
| EventBus | 设备与任务状态变化从 StateCenter 发布 |
| SignalR | negotiate、WebSocket 握手、Hub 方法调用、业务广播接收 |
| 内存 | 每秒采集 Host RSS，记录初始、最终和峰值 |

## 4. 自动化入口

工作流：

```text
.github/workflows/e2e-load.yml
```

负载程序：

```text
tests/load/wcs-e2e-load.mjs
```

PR 默认回归参数：

```text
持续时间：60 秒
HTTP 并发：16
SignalR 连接：50
测试任务写入：1 次/秒
```

手工运行默认参数：

```text
持续时间：300 秒
HTTP 并发：32
SignalR 连接：100
测试任务写入：1 次/秒
```

长稳测试可通过 `workflow_dispatch` 增加持续时间，工作流最长允许运行 90 分钟。

## 5. 默认判定门槛

```text
HTTP 错误率 <= 1%
HTTP P95 <= 1000 ms
SignalR 成功连接数 = 请求连接数
SignalR 业务消息数 >= 1
模拟 PLC 实时设备数 > 0
SQL Server 持久化设备数 > 0
SQL Server 完成任务证据数 > 0
测试任务写入接口无失败
负载结束后 /health/live 正常
```

高于这些门槛不一定表示现场不可用，但必须先分析错误、延迟、数据库争用和内存趋势后才能调整门槛。

## 6. 证据文件

每次运行保留 14 天：

```text
host.log
e2e-load-results.json
sql-evidence.txt
```

`e2e-load-results.json` 包含：

- 总请求、失败请求和错误率；
- RPS、P50、P95、P99 和最大延迟；
- 各接口状态码分布；
- SignalR 连接、订阅和消息类型统计；
- Host 初始、最终和峰值 RSS；
- 实时设备、持久化设备、持久化任务和最终存活检查；
- 所有未通过门槛。

## 7. 结果解释

GitHub Runner 是共享虚拟机，因此结果适合：

- 检查版本间性能回退；
- 暴露并发异常、数据库连接问题和 SignalR 断连；
- 验证模拟 PLC 到 SQL/SignalR 的完整链路。

结果不适合直接承诺：

- 现场每小时任务能力；
- 实车数量和路权容量；
- PLC 网络时延；
- 正式 SQL Server 磁盘能力；
- 24 小时以上内存稳定性。

正式上线前仍需在目标服务器和现场网络执行持续班次压测及实车验收。
