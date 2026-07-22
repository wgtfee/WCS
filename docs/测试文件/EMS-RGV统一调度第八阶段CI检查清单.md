# EMS / RGV 统一调度第八阶段 CI 检查清单

Windows CI 必须通过：

```text
Build and test Wcs.Core
Build Wcs.Host
Build Wcs.Desktop
```

重点检查：

- JSON、CSV、XLSX 点位表导入；
- enum 字典 JSON 转换；
- 通信跟踪包装器 DI 注册；
- 新增治理操作权限映射完整；
- Stop-only 命令补偿边界；
- SqlSugar 联调实体 CodeFirst；
- multipart 文件接口和 JSON 请求模型；
- Avalonia DataGrid、UniformGrid 和 RelayCommand 绑定；
- Desktop 不包含绕过审批的危险操作入口。
