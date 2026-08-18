# WCS Runtime Engine

面向工业现场的仓储控制与输送调度系统，基于 **.NET 8** 构建，围绕 PLC 实时状态、任务调度、设备控制、EMS/RGV 交通调度、故障恢复、仿真验证和运行可观测性提供统一运行时能力。

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
- **异常与健康分析**：包含 PLC 异常检测、资产健康评分、根因关联、维修决策、RUL/故障概率等扩展模块。
- **统一仿真验证**：支持虚拟 PLC、虚拟 RGV、交通冲突、外部依赖故障、健康退化、全链路恢复、容量长稳和 HIL 准备验证。
- **桌面操作端**：Wcs.Desktop 提供工业运行、设备、任务、报警、仿真和可视化操作界面。
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

仓库同时包含工业智能、维护学习、优化等扩展项目，以及大量专项 CI、容量测试和仿真验证工作流。

## 快速开始

### 环境要求

- .NET 8 SDK
- Windows 为主要桌面与现场运行环境
- SQL Server：需要数据库持久化能力时配置
- 真实 PLC：仅在真实设备模式下需要

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

具体连接字符串、PLC 地址、机架/槽位、IAM Authority/Audience 等均属于部署环境参数，不应把生产敏感值直接提交到仓库。

## 仿真与验证

项目内置统一仿真验证体系，覆盖：

- PLC 断线、恢复与信号故障注入
- 虚拟 RGV 运动与区段占用
- 双车交通冲突与死锁场景
- 外部接口超时、重试与恢复
- 健康评分与 RUL 合成退化场景
- 全链路任务恢复与状态一致性验证
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

详细说明：

- `docs/mcp-readonly-adapter.md`
- `docs/mcp-iam-boundary.md`

## 分支说明

- `main`：稳定主分支，用于正式集成版本。
- `develop`：日常开发与阶段集成分支。
- `Dev_IAM`：IAM 相关长期开发/验证分支。

功能分支通过 PR 合入后建议自动删除，避免长期积累废弃分支。

## 文档

完整设计、测试、部署、调度、PLC、仿真、异常分析和交付资料位于：

```text
docs/
├─ 交付文件/
├─ 架构文件/
├─ 测试文件/
└─ 设计流程文件/
```

其中 `docs/交付文件/00-交付文档总索引.md` 可作为完整文档入口。

## 生产安全原则

- 仿真、AI、MCP 等扩展能力不得绕过 WCS 正式控制链路。
- PLC 写入、设备执行、路权和交通控制必须由正式领域逻辑负责。
- 生产控制默认采用确定性和失效关闭策略。
- 外部系统只能通过明确的接口和权限边界访问 WCS 能力。
- 任何现场上线前都应完成对应的仿真、回归、容量和现场联调验证。
