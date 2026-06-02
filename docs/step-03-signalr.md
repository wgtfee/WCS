# Step 3：SignalR 集成 + ASP.NET Core 宿主

## 目标

将 Host 从 Worker Service 升级为 ASP.NET Core 宿主，承载 SignalR 实时推送能力。

## 改动文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/Wcs.Host/Wcs.Host.csproj` | 修改 | SDK 改为 `Microsoft.NET.Sdk.Web` |
| `src/Wcs.Host/Program.cs` | 修改 | `WebApplication.CreateBuilder` + `AddSignalR()` + `MapHub` |
| `src/Wcs.Infrastructure/SignalR/WcsHub.cs` | 已存在 | 无需改动 |

## SignalR 推送通道

| 通道 | 触发时机 | 推送数据 |
|------|---------|---------|
| `DeviceStateChanged` | StateCenter 设备状态变化 | `{ deviceId, status, lastUpdateTime }` |
| `TaskStateChanged` | 任务状态变化 | `{ taskId, status, priority }` |
| `AlarmEvent` | 报警产生/恢复 | `{ action, alarm }` |
| `ObjectMoved` | 物料位置变化 | `{ objectId, oldPos, newPos }` |

## 启动方式变化

```
Worker Service:  Host.CreateApplicationBuilder → Build → RunAsync
ASP.NET Core:    WebApplication.CreateBuilder → Build → MapHub → RunAsync
```

两者兼容 — BackgroundService、Windows Service 照常运行。

## 验证

1. `dotnet build` 0 错误
2. 启动后，`http://localhost:5000/wcs` 可建立 SignalR 连接
