# Step 4：健康检查端点

## 目标

暴露 HTTP 健康检查端点，用于系统监控和 Docker/K8s 探针。

## 改动文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/Wcs.Host/HealthChecks/WcsHealthCheck.cs` | 新建 | 实现 `IHealthCheck`，检查 StateCenter / PLC / DB |
| `src/Wcs.Host/Program.cs` | 修改 | `AddHealthChecks()` + `MapHealthChecks()` |

## 端点

| 路径 | 用途 |
|------|------|
| `/health/ready` | 就绪探针 - 检查 DB 连接和 StateCenter |
| `/health/live` | 存活探针 - 检查进程是否响应 |
| `/health` | 聚合状态 |

## 验证

1. `dotnet build` 0 错误
2. `curl http://localhost:5000/health` 返回 `{"status":"Healthy"}`
