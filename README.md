# WCS Runtime Engine

面向工业现场的仓储控制与输送调度系统，基于 **.NET 8** 构建，围绕 PLC 实时状态、任务调度、设备控制、EMS/RGV 交通调度、故障恢复、工业智能、模型治理、仿真验证和运行可观测性提供统一运行时能力。

项目的核心原则是：**业务与控制逻辑集中在 Wcs.Core，上层仅负责组合、托管和展示；生产控制路径保持确定性、可审计、失效关闭。**

## 核心能力

- **PLC 与设备接入**：支持 Siemens S7 相关接入，并提供 S7CommPlus、Modbus、OPC UA 等扩展能力。
- **实时状态中心**：以 StateCenter 维护设备、任务、报警、对象等运行态，减少运行期对数据库的高频轮询依赖。
- **任务调度与编排**：TaskScheduler、TaskOrchestrator、TaskChainEngine、StateMachine 等组件负责任务生命周期和流程执行。
- **EMS / RGV 调度**：覆盖车辆、路径、区段、路权、交通冲突、死锁检测、执行、恢复和生产调度等能力。
- **资源与安全控制**：ResourceLockManager、DeadlockDetector、Route/Traffic 相关组件用于资源互斥、冲突检测和安全边界控制。
- **报警与恢复**：支持报警治理、状态恢复、快照、持久化和重启恢复。
- **对象追踪**：跟踪载荷、设备和任务在运行过程中的位置与状态变化。
- **运行可观测性**：提供日志、指标、健康检查、OpenTelemetry、SignalR 实时状态发布等能力。
- **工业模型与 AI**：覆盖 PLC 机器学习异常检测、模型版本治理、本地 ONNX 推理、异常证据融合、资产健康、根因分析、维修决策、故障概率与 RUL、ModelOps、影子决策、维护学习和数字孪生优化。
- **统一仿真验证**：支持虚拟 PLC、虚拟 RGV、交通冲突、外部依赖故障、健康退化、全链路恢复、容量长稳和 HIL 准备验证。
- **桌面操作端**：Wcs.Desktop 提供工业运行、设备、任务、报警、工业智能、模型治理、仿真和可视化操作界面。
- **只读 MCP 接入**：可向上层 AI/Agent 平台暴露经过约束的 WCS 只读状态工具，并通过 JWT/IAM 边界保护接口。

## 架构分层

```text
Wcs.Desktop
    │  Avalonia 工业桌面端
    ▼
Wcs.Host
    │  ASP.NET Core / SignalR / HealthCheck / MCP / 后台托管
    ▼
Wcs.Application
    │  DI、应用服务、HostedService 组合
    ▼
Wcs.Core
    │  状态、调度、编排、设备、报警、交通、恢复等核心业务逻辑
    ▼
Wcs.Infrastructure
       SQL、PLC、SignalR、日志、持久化等基础设施
```

仿真能力由 `Wcs.Simulator` 与相关验证组件提供，并尽量复用正式运行时契约，避免形成与生产逻辑脱节的另一套实现。

## 主要运行链路

```text
PLC / Simulator
      ↓
PlcPolling / Block Diff / Event Detection
      ↓
EventBus / StateCenter
      ↓
TaskScheduler / AlarmCenter / Traffic
      ↓
TaskOrchestrator / Execution
      ↓
DeviceManager / Transport Execution
      ↓
SignalR / Desktop / Metrics / Persistence
```

工业智能链路与生产控制链路保持隔离：

```text
Telemetry / Runtime State / History
            ↓
Feature / Context / Peer Comparison
            ↓
Anomaly / Health / Forecast Models
            ↓
Evidence Fusion / Root Cause / Decision Intelligence
            ↓
Shadow Proposal / Maintenance Learning / Optimizer
            ↓
Desktop / API / MES / MCP 只读输出

            × 不直接写 PLC
            × 不绕过 CommandBus
            × 不自动替换生产调度策略
```

## 主要项目

```text
src/
├─ Wcs.Core                核心领域与运行时逻辑
├─ Wcs.Application         应用层与依赖注入
├─ Wcs.Infrastructure      数据库、PLC、日志、SignalR 等基础设施
├─ Wcs.Host                ASP.NET Core 运行宿主
├─ Wcs.Desktop             Avalonia 桌面客户端
├─ Wcs.Simulator           仿真运行能力
└─ Wcs.Core.Tests          核心单元与集成测试
```

仓库同时包含工业智能、模型治理、维护学习、决策智能、优化等扩展项目，以及大量专项 CI、容量测试和仿真验证工作流。

## 模型与工业 AI

WCS 内的 AI 主要面向**工业机器学习、设备健康分析和受治理的决策智能**。它不是让大模型直接接管 PLC 或调度，而是在正式控制链路之外提供检测、预测、评估、建议和治理能力。

### PLC 机器学习异常检测

已包含 PLC ML 异常分析能力，包括：

- PLC 运行窗口与确定性特征生成
- 运行上下文识别与同群/同类设备对比
- 异常模型训练、推理和结果持久化
- 模型版本管理、候选模型验证和独立审批
- 模型激活与安全回滚
- 模型治理 API 与运行状态查询
- 规则、统计、机器学习等多来源异常证据协同

### 可插拔本地模型运行时

模型运行层支持受治理的本地推理：

- **ONNX Runtime CPU** 本地推理
- 可插拔模型适配器
- 模型 Manifest、特征 Schema 和完整性校验
- 模型加载、推理、失败恢复和生命周期状态
- 本地 Isolation Forest 等适配能力
- 生产配置默认关闭外部/可插拔模型运行能力，显式启用后才进入运行链路

本地模型设计的目标是：在工业现场网络受限或不适合依赖云模型的情况下，仍能完成可重复、低延迟、可治理的模型推理。

### 异常证据融合与资产健康

异常分析已经从单一告警扩展到资产级健康链路，包含：

- 多模型异常证据融合
- 运输动作周期与阶段异常分析
- 资产健康评分
- 健康历史持久化与重启恢复
- 健康事件治理与 MES 联动
- 根因关联与异常传播分析
- 维修决策支持与反馈闭环
- 故障概率预测
- **RUL（Remaining Useful Life，剩余使用寿命）预测**

### ModelOps Center

工业模型不是直接把一个模型文件丢进生产环境运行，而是通过 ModelOps 进行治理，当前包含：

- 模型注册与版本管理
- 模型包/Manifest 校验
- 候选模型与部署状态治理
- SQL 持久化的模型注册、部署和审计记录
- Shadow Runtime / 影子运行
- 模型审批、激活、回滚和生命周期管理
- Host API 与 Desktop 模型治理界面

### Industrial Decision Intelligence v4.0

仓库已经实现分阶段的工业决策智能体系：

| 阶段 | 能力 | 当前边界 |
|---|---|---|
| IDI-P0 | 治理与契约基线 | 建立零生产控制依赖和失效关闭边界 |
| IDI-P1 | ModelOps Center | 模型注册、部署、影子运行、治理与恢复 |
| IDI-P2 | Feature Center | 特征契约、特征治理和决策输入标准化 |
| IDI-P3 | Shadow Decision | 生成受治理的影子决策建议，不直接执行生产控制 |
| IDI-P4 | Maintenance Learning | 维修结果、MES/Outbox 反馈和闭环学习证据 |
| IDI-P5 | Digital Twin Optimizer | 基于 S0-S10 仿真进行多目标策略比较与优化建议 |
| IDI-P6 | Bounded Automation Readiness | 建立有限自动化的软件治理与证据基础，生产自动控制仍保持关闭 |

### AI / 模型安全边界

工业智能模块遵循以下原则：

- AI/模型默认不拥有 PLC 写权限。
- AI/模型不得绕过 `CommandBus`、`TaskScheduler`、`TaskOrchestrator`、`DeviceManager` 或 Traffic/RouteReservation 正式控制链路。
- 模型输出优先作为异常证据、评分、预测、建议或影子决策。
- 模型升级必须经过版本、验证、审批、影子运行和回滚治理。
- Digital Twin Optimizer 只生成策略比较和优化建议，不自动替换生产策略。
- Bounded Automation Readiness 当前表示软件侧治理与证据准备就绪，不等于允许 AI 自动控制现场设备。
- 真正的状态变更必须继续满足 WCS 领域规则、权限、风险控制和现场安全条件。

## 快速开始

### 环境要求

- .NET 8 SDK
- Windows 为主要桌面与现场运行环境
- SQL Server：需要数据库持久化能力时配置
- 真实 PLC：仅在真实设备模式下需要
- ONNX 模型：仅在启用对应本地模型运行功能时需要

建议先使用模拟模式完成本地功能验证，再连接现场 PLC。

### 还原与构建

```bash
dotnet restore src/Wcs.Host/Wcs.Host.csproj
dotnet build src/Wcs.Host/Wcs.Host.csproj -c Release
```

### 运行核心测试

```bash
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj -c Release
```

### 启动 Host

```bash
dotnet run --project src/Wcs.Host/Wcs.Host.csproj
```

### 启动 Desktop

```bash
dotnet run --project src/Wcs.Desktop/Wcs.Desktop.csproj
```

具体连接字符串、PLC 地址、机架/槽位、模型路径、IAM Authority/Audience 等均属于部署环境参数，不应把生产敏感值直接提交到仓库。

## 仿真与验证

项目内置统一仿真验证体系，覆盖：

- PLC 断线、恢复与信号故障注入
- 虚拟 RGV 运动与区段占用
- 双车交通冲突与死锁场景
- 外部接口超时、重试与恢复
- 健康评分与 RUL 合成退化场景
- 全链路任务恢复与状态一致性验证
- 模型、健康和决策智能相关确定性验证
- Digital Twin Optimizer 策略实验
- 容量、稳定性、Soak、E2E 和 HIL readiness

Desktop 中的仿真验证界面以中文操作流程为主，可在没有真实 PLC/设备的情况下完成大量功能和回归验证。

## MCP 与 IAM 安全边界

WCS 的 MCP 接入默认关闭，只暴露受控的**只读状态能力**。当前边界明确禁止通过 MCP 直接执行：

```text
CommandBus 写命令
PLC 写入
设备控制命令
任务创建/取消
报警确认/恢复
SQL 状态修改
```

启用 MCP 时必须配置有效的 JWT `Authority` 和 `Audience`，缺少必要 IAM 配置时采用 **fail-closed** 策略，不开放接口。

MCP 的定位是把 WCS 受控状态能力提供给上层 AI/Agent 平台；即使上层 Agent 具备更强的推理能力，也不能绕过 WCS 自身的权限和生产控制边界。

详细说明：

- `docs/mcp-readonly-adapter.md`
- `docs/mcp-iam-boundary.md`

## 分支说明

- `main`：稳定主分支，用于正式集成版本。
- `develop`：日常开发与阶段集成分支。
- `Dev_IAM`：IAM 相关长期开发/验证分支。

功能分支通过 PR 合入后建议自动删除，避免长期积累废弃分支。

## 文档

完整设计、测试、部署、调度、PLC、仿真、模型、工业智能和交付资料位于：

```text
docs/
├─ 交付文件/
├─ 架构文件/
├─ 测试文件/
└─ 设计流程文件/
```

其中 `docs/交付文件/00-交付文档总索引.md` 可作为完整文档入口。

模型与工业智能相关资料包括 PLC 异常检测、模型治理、健康评分、根因分析、维修闭环、ONNX 运行时、故障概率/RUL、Industrial Decision Intelligence v4.0、ModelOps、Shadow Decision、Maintenance Learning、Digital Twin Optimizer 和 Bounded Automation Readiness 等专项文档。

## 生产安全原则

- 仿真、AI、模型、MCP 等扩展能力不得绕过 WCS 正式控制链路。
- PLC 写入、设备执行、路权和交通控制必须由正式领域逻辑负责。
- AI/模型输出默认作为分析、预测、建议和影子决策，不默认获得生产执行权。
- 生产控制默认采用确定性和失效关闭策略。
- 外部系统只能通过明确的接口和权限边界访问 WCS 能力。
- 任何模型升级都应具备验证、审批、影子运行、监控和回滚能力。
- 任何现场上线前都应完成对应的仿真、回归、容量和现场联调验证。
