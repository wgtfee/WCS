# EMS/RGV 统一调度第十一步测试方案

## 1. 测试目标

验证生产就绪检查、运行基线、离线逻辑备份、SHA-256 校验、恢复准备和隔离恢复演练在不影响调度控制层的前提下稳定运行。

## 2. 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportResilienceTests.cs
```

覆盖场景：

1. 内存备份存储按时间保留最新 N 份；
2. 创建逻辑备份后 SHA-256、Schema 和 JSON 均有效；
3. 篡改载荷后必须识别 HashMismatch；
4. 恢复准备只创建配置快照，不应用运行配置；
5. 隔离演练不修改车辆注册表；
6. 真实 PLC 点位缺少在线诊断时生产就绪必须出现 Critical。

## 3. Host 构建验证

```powershell
dotnet restore src/Wcs.Host/Wcs.Host.csproj
dotnet build src/Wcs.Host/Wcs.Host.csproj -c Release --no-restore
```

检查：

- `TransportResilienceController` 路由无冲突；
- `FileTransportLogicalBackupStorage` 可创建目录；
- `TransportResilienceOptions` 能从 appsettings 加载；
- HealthCheck 可解析 `ITransportResilienceService`；
- 自动备份服务启动后不会立即执行备份；
- 备份失败不会终止 Host。

## 4. Desktop 构建验证

```powershell
dotnet restore src/Wcs.Desktop/Wcs.Desktop.csproj
dotnet build src/Wcs.Desktop/Wcs.Desktop.csproj -c Release --no-restore
```

检查：

- 菜单可打开 `TransportResilienceViewModel`；
- ViewLocator 可解析 `TransportResilienceView`；
- 生产就绪、基线、备份、校验问题和演练页签可显示；
- 选中备份后可执行校验；
- 页面不存在恢复、PLC 写入或故障注入按钮。

## 5. 生产就绪测试

### 5.1 模拟模式

预期：

- PLC 驱动检查显示模拟模式；
- 无运行配置、无安全快照、无备份可产生 Warning；
- 不应产生 PLC Critical。

### 5.2 真实 PLC 模式

构造：

- 启用车辆；
- 启用真实驱动；
- 配置 PlcTag 点位；
- 不写入在线诊断。

预期：

- `PlcDriverFreshness` 为 Critical；
- `IsReady=false`；
- `/health/ready` 返回 Unhealthy。

### 5.3 状态存储不可用

预期：

- `RuntimeStateStore` 为 Critical；
- Host 继续运行；
- 不触发自动任务恢复或 PLC 重发。

## 6. 逻辑备份测试

### 6.1 创建

检查：

- 载荷文件和 `.manifest.json` 同时存在；
- 临时文件已清理；
- Manifest 的大小与实际文件一致；
- SHA-256 为 64 位小写十六进制；
- Journal 存在 `LogicalBackup` 记录。

### 6.2 篡改

修改载荷任意字节后执行：

```text
POST /api/transport/resilience/backups/{backupId}/validate
```

预期：

- `HashValid=false`；
- 出现 `HashMismatch/Critical`；
- `CanPrepareConfigurationRestore=false`。

### 6.3 保留策略

创建超过 `BackupRetentionCount` 的备份。

预期：

- 只保留最新 N 份；
- Manifest 和载荷同步删除；
- 当前运行数据库不受影响。

## 7. 恢复准备测试

### 7.1 有效备份

预期：

- 生成新的配置 SnapshotId；
- 当前运行配置版本不变化；
- 当前整定版本不变化；
- 返回 PLC 点位、活动任务、路权和命令人工恢复清单。

### 7.2 无效备份

预期：

- 不创建配置快照；
- 返回 Conflict；
- 不修改运行配置。

### 7.3 真正配置恢复

使用导入 SnapshotId 走现有双人审批回滚流程。

预期：

- 申请人和审批人必须不同；
- 执行前生成安全快照；
- 版本冲突时拒绝执行；
- 活动任务和 PLC 命令不自动恢复。

## 8. 隔离演练测试

对每个场景执行演练前后记录：

- 车辆快照；
- 执行任务；
- 活动路权；
- PLC 诊断；
- SQL 状态。

预期：

- 演练报告 `IsIsolatedSimulation=true`；
- 车辆、任务、路权、PLC 和 SQL 均无变化；
- 演练步骤包含明确预期结果；
- 演练结果写入 `RecoveryDrill` Journal。

## 9. 离线现场验收

1. 断开外网；
2. 启动 WCS Host；
3. 确认自动备份写入本地目录；
4. 重启 Host 后可读取历史 Manifest；
5. 下载备份并在另一台离线机器校验 SHA-256；
6. 准备恢复快照，但不执行回滚；
7. 导出生产韧性报告；
8. 确认整个过程不依赖云服务、MQ 或外部对象存储。
