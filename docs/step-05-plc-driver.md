# Step 5：PLC 真实驱动 (S7netplus)

## 目标

将模拟 `S7Connection` 替换为基于 S7netplus 的真实 PLC 驱动。

## 改动文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/Wcs.Infrastructure/S7/S7RealClient.cs` | 修改 | 用 S7netplus `Plc` 类替换模拟代码 |
| `src/Wcs.Infrastructure/Wcs.Infrastructure.csproj` | 修改 | 添加 `S7netplus` NuGet 包 |
| `src/Wcs.Host/Program.cs` | 修改 | 注册 `S7RealClient` 替代 `S7Connection` |

## 工厂模式

提供 `IS7ConnectionFactory` 接口从配置创建连接，支持多 PLC 配置：

```
appsettings.json → PlcConnections[]
  → S7ConnectionFactory.CreateAll()
    → List<IS7Connection> 注入 PlcPollingService
```

## 兼容性

- `IS7Connection` 接口不变，下游代码无需改动
- 保留 `S7Connection`（模拟）作为开发/测试回退

## 验证

1. `dotnet build` 0 错误
2. 连接真实 PLC 时可读到数据块
