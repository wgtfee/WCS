# Step 6: Avalonia 跨平台桌面监控客户端

## 目标
为 WCS Runtime Engine 提供跨平台桌面 GUI 客户端，替代 WinForms，使用 Avalonia 框架实现 Windows/Linux/macOS 支持。

## 架构设计

```
Wcs.Desktop (Avalonia 11)
  ├── REST API (HttpClient) → Wcs.Host/WcsController
  └── SignalR (HubConnection) → Wcs.Host/WcsHub
```

### 通信模式
- **REST API**: 初始数据加载和用户操作（创建任务、确认报警等）
- **SignalR**: 实时状态推送（设备状态变化、任务事件、报警、物体移动）

## 新增文件

### Wcs.Host (`src/Wcs.Host/Controllers/WcsController.cs`)
新 REST API 控制器，委托给 `WcsApplicationService`：
- `GET /api/overview` — 系统概览
- `GET /api/devices` — 所有设备
- `GET /api/devices/{deviceId}` — 单个设备
- `GET /api/tasks` / `POST /api/tasks` — 任务查询/创建
- `POST /api/tasks/{taskId}/cancel` — 取消任务
- `POST /api/tasks/{taskId}/complete` — 完成任务
- `GET /api/alarms` — 活跃报警
- `POST /api/alarms/{alarmId}/ack` — 确认报警
- `POST /api/alarms/{alarmCode}/recover` — 恢复报警
- `GET /api/objects` / `GET /api/locks` — 物体/锁查询
- `POST /api/system/recover` — 系统恢复

### Wcs.Infrastructure (`src/Wcs.Infrastructure/SignalR/Messages.cs`)
SignalR 命名记录类型，替代匿名类型，确保客户端正确反序列化：
- `DeviceStateChangedMessage`
- `TaskStateChangedMessage`
- `AlarmEventMessage`
- `ObjectMovedMessage`

### Wcs.Desktop — 新 Avalonia 项目

| 类别 | 文件 | 说明 |
|------|------|------|
| **项目** | `Wcs.Desktop.csproj` | net8.0, Avalonia 11.1, CommunityToolkit.Mvvm, SignalR Client |
| **入口** | `Program.cs` / `App.axaml` / `App.axaml.cs` | DI 容器搭建，ViewLocator |
| **配置** | `appsettings.json` | 服务器地址、重连参数 |
| **Services** | `WcsApiService.cs` | HttpClient REST 客户端 |
| | `WcsRealtimeService.cs` | SignalR HubConnection 包装（自动重连） |
| | `ServiceCollectionExtensions.cs` | DI 注册 |
| | `WcsDesktopOptions.cs` | 配置强类型绑定 |
| **Models** | `DeviceItem.cs`, `TaskItem.cs`, `AlarmItem.cs`, `ObjectItem.cs`, `EventLogEntry.cs`, `ConnectionState.cs` | 显示模型 |
| **ViewModels** | `MainWindowViewModel.cs` | Tab 导航宿主，连接管理 |
| | `DashboardViewModel.cs` | 概览卡片（设备/任务/报警/锁计数） |
| | `DeviceListViewModel.cs` | 设备列表 + 实时状态更新 |
| | `TaskManagementViewModel.cs` | 任务列表 + 创建/取消 |
| | `AlarmPanelViewModel.cs` | 报警列表 + 确认/恢复 |
| | `ObjectTrackingViewModel.cs` | 物体追踪列表 |
| | `EventLogViewModel.cs` | 实时事件流日志 |
| **Views** | `MainWindow.axaml` | TabControl 布局，ConnectionBar |
| | `DashboardView.axaml` | 5 个 OverviewCard 卡片 |
| | `DeviceListView.axaml` | 设备列表，状态指示点 |
| | `TaskManagementView.axaml` | 创建任务面板 + 任务列表 |
| | `AlarmPanelView.axaml` | 报警列表 + 级别颜色 |
| | `ObjectTrackingView.axaml` | 物体追踪列表 |
| | `EventLogView.axaml` | 实时日志流 |
| **Controls** | `OverviewCard.axaml` | 概览数字卡片 |
| | `ConnectionBar.axaml` | 连接状态栏 |
| **Converters** | `StatusToColorConverter.cs` | 状态→颜色映射 |
| | `BoolToVisibilityConverter.cs` | 布尔→可见性 |
| | `InverseBoolConverter.cs` | 布尔取反 |
| **Styles** | `Colors.axaml`, `Themes.axaml` | 深色主题调色板 |

## 修改文件

| 文件 | 改动 |
|------|------|
| `WcsEngine.slnx` | 加入 `Wcs.Desktop.csproj` |
| `src/Wcs.Host/Program.cs` | 加 `AddControllers()` + `MapControllers()` |
| `src/Wcs.Infrastructure/SignalR/WcsHub.cs` | 匿名类型 → 命名记录类型 |
| `src/Wcs.Core/StateCenter/Models/StateModels.cs` | 加入 `SystemOverview` DTO（共用类型） |
| `src/Wcs.Application/Services/WcsApplicationService.cs` | 移除 `SystemOverview`（移至 Core） |

## 数据流

**初始加载**: ViewModel 激活 → `IWcsApiService.GetXxxAsync()` → 填充 ObservableCollection
**实时更新**: SignalR 推送 → `WcsRealtimeService` → `Dispatcher.Invoke` → UI 更新
**用户操作**: 按钮 → `RelayCommand` → `IWcsApiService` POST → REST 端点 → EventBus → SignalR 推送确认

## 验证方式

1. `dotnet build WcsEngine.slnx` — 0 错误
2. 启动 Wcs.Host，`curl http://localhost:5000/api/overview` 返回 JSON
3. 启动 Wcs.Desktop，连接状态栏显示绿色 "Connected"
4. Dashboard 卡片显示正确数字
5. 设备列表实时更新状态指示点
6. 创建/取消任务正常
7. 报警确认/恢复双向同步

## 下一步建议

1. **身份验证** — 为 SignalR 和 REST API 添加 JWT 认证
2. **布局保存** — 记住窗口位置、大小、选中 Tab
3. **国际化** — 中英文界面切换
4. **图表** — 接入 LiveCharts2 显示设备运行趋势
5. **PLC 数据监控** — 实时显示 PLC 数据块值
