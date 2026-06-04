# Step 2：配置热重载 (IOptions)

## 目标

将硬编码的轮询/持久化间隔提升为 `appsettings.json` 可配置项，并支持运行时热重载。

## 改动文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/Wcs.Core/Common/Options/WcsOptions.cs` | 新建 | 配置模型类 |
| `src/Wcs.Host/appsettings.json` | 修改 | 增加 WcsOptions 节 |
| `src/Wcs.Host/Program.cs` | 修改 | `Configure<WcsOptions>()` 绑定 |
| `src/Wcs.Host/BackgroundServices/*.cs` (4个) | 修改 | 用 `IOptionsMonitor<T>` 替换硬编码间隔 |

## 配置模型

```
WcsOptions
├── PlcPolling: { IntervalMs: 100 }
├── Persistence: { IntervalSeconds: 10 }
├── Snapshot: { IntervalSeconds: 5, MaxSnapshots: 100 }
└── AlarmMonitor: { IntervalSeconds: 10 }
```

## 热重载机制

- `IOptionsMonitor<T>.OnChange` 监听文件变更
- 每个 BackgroundService 的循环中读取 `_options.CurrentValue`
- 修改 `appsettings.json` 无需重启服务

## 验证

1. `dotnet build` 0 错误
2. 运行时修改 `appsettings.json` 中 interval，下一周期自动生效
