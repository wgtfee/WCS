# Step 1：持久化后台服务对接 Dapper 仓库

## 目标

让 `PersistBackgroundService` 真正将 `StateCenter` 中的运行时数据写入 SQL Server，打通数据流末端。

## 改动文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/Wcs.Host/BackgroundServices/PersistBackgroundService.cs` | 修改 | 注入 Dapper 仓库，每周期写入 TaskRuntime / DeviceRuntime / AlarmRuntime |
| `src/Wcs.Infrastructure/Persistence/Repositories/TaskRepository.cs` | 修改 | 补充 `SaveDeviceRuntimeAsync` 的 Dapper SQL |

## 数据流

```
StateCenter (内存)
   │
   └── PersistBackgroundService (每 10s)
         ├── TaskRuntime → TaskRepository.SaveTaskRuntimeAsync()
         ├── DeviceState → TaskRepository.SaveDeviceRuntimeAsync()
         └── AlarmState  → AlarmRepository.SaveAlarmRuntimeAsync()
```

## 要点

- 保留 StateCenter 作为"系统真相"，数据库只做持久化不反驱
- 使用 Dapper MERGE (upsert) 确保幂等
- 写库失败不中断主流程，仅打 Warning 日志

## 验证

1. `dotnet build` 0 错误
2. 启动后 10 秒，SQL Server 中看到三张 Runtime 表写入数据
