# EMS / RGV 统一调度第二阶段测试方案

## 1. 验收目标

验证第二阶段执行引擎在单实例内存模型下满足：

- 任务状态迁移正确。
- 位置反馈去重和乱序保护有效。
- 滚动预留窗口正确推进。
- 已通过路段及时释放。
- 前方冲突时安全等待。
- 装卸确认后任务正确完成。
- 暂停、恢复、故障、取消不破坏资源状态。
- 逻辑命令生成顺序正确。

## 2. 自动化测试

测试文件：

```text
src/Wcs.Core.Tests/TransportExecutionEngineTests.cs
```

### 2.1 初始窗口

输入完整路径 `E1-E5`、窗口大小 2。

期望派单后仅预留：

```text
E1, E2
```

### 2.2 位置推进

车辆从 `N1` 上报到达 `N2`。

期望：

- 释放 `E1`
- 保持 `E2`
- 新增 `E3`
- 活动窗口为 `E2,E3`

### 2.3 乱序反馈

先接收 Sequence=5，再接收 Sequence=4。

期望：

- 第二次反馈失败
- 当前节点不变化
- 路段预留不变化

### 2.4 前方冲突

其他任务提前占用 `E3`，当前车辆到达 `N2`。

期望：

- `E1` 被释放
- `E3` 扩展失败
- 任务进入 `WaitingForRoute`
- 不生成继续移动命令

### 2.5 完整生命周期

```text
Assigned
-> MovingToPickup
-> Loading
-> MovingToDestination
-> Unloading
-> Completed
```

完成后期望：

- 全部路段释放
- 派单记录完成
- 车辆恢复 Idle
- ActiveTaskCount 恢复 0

### 2.6 暂停恢复

期望：

- Pause 生成 Stop 命令
- Resume 重新确认前方窗口
- 预留成功后生成 MoveToNode 命令

## 3. Windows CI

CI 应执行：

```powershell
dotnet restore src/Wcs.Core.Tests/Wcs.Core.Tests.csproj
dotnet build src/Wcs.Core.Tests/Wcs.Core.Tests.csproj -c Release --no-restore
dotnet test src/Wcs.Core.Tests/Wcs.Core.Tests.csproj -c Release --no-build
```

Desktop 页面加入后还应执行：

```powershell
dotnet restore src/Wcs.Desktop/Wcs.Desktop.csproj
dotnet build src/Wcs.Desktop/Wcs.Desktop.csproj -c Release --no-restore
```

## 4. 现场联调前附加测试

接入真实控制器前必须补充：

- PLC 位置反馈抖动。
- 反馈丢包后的序号跨越。
- 控制器重复回执。
- 通讯断线期间的安全停车。
- WCS 恢复连接后的当前位置对账。
- 人工移动设备后的路径不一致处理。
