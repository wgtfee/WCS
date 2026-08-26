using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using Wcs.Desktop.Controls;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Services;
using Wcs.Desktop.Models;
using Wcs.Entity;

namespace Wcs.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncInitializable
{
    private readonly IWcsRealtimeService _realtime;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDataProvider _dataProvider;
    private readonly ClosableTabItem _homeTab;

    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private ClosableTabItem? _selectedTabItem;
    [ObservableProperty] private ObservableCollection<MenuItemDto> _menuItems = new();
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private MenuItemDto? _selectedMenuItem;
    [ObservableProperty] private bool _isCollapsedFlyoutOpen;
    [ObservableProperty] private ObservableCollection<MenuItemDto>? _activeFlyoutChildren;
    [ObservableProperty] private MenuItemDto? _activeFlyoutSelectedItem;

    public double SidebarWidth => IsSidebarCollapsed ? 68 : 264;
    public NotificationCenterViewModel NotificationCenter { get; } = new();
    public ObservableCollection<ClosableTabItem> Tabs { get; } = new();

    public MainWindowViewModel(IWcsRealtimeService realtime, IWcsApiService api, IOptions<WcsDesktopOptions> options, IServiceProvider serviceProvider, DashboardViewModel dashboard, IDataProvider dataprovider)
    {
        _realtime = realtime;
        _serviceProvider = serviceProvider;
        _dataProvider = dataprovider;
        _homeTab = new ClosableTabItem { Header = "运行总览", Content = dashboard, CanClose = false, IsSelected = true, Description = "全局概览：设备、任务、报警、追踪对象的实时数量与快捷入口。" };
        Tabs.Add(_homeTab);
        SelectedTabItem = _homeTab;
        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
        _ = InitializeMenuAsync(api);
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        if (!value)
            CloseCollapsedFlyout();
    }

    partial void OnSelectedMenuItemChanged(MenuItemDto? value)
    {
        if (value != null && (value.Children == null || value.Children.Count == 0))
        {
            _ = OpenPageFromMenu(value);
            SelectedMenuItem = null;
        }
    }

    partial void OnActiveFlyoutSelectedItemChanged(MenuItemDto? value)
    {
        if (value == null)
            return;
        if (value.Children.Count > 0)
            ActiveFlyoutChildren = value.Children;
        else
        {
            _ = OpenPageFromMenu(value);
            CloseCollapsedFlyout();
        }
        ActiveFlyoutSelectedItem = null;
    }

    [RelayCommand]
    private void CollapsedMenuClick(MenuItemDto item)
    {
        if (item.Children.Count > 0)
        {
            ActiveFlyoutChildren = item.Children;
            IsCollapsedFlyoutOpen = true;
        }
        else if (!string.IsNullOrEmpty(item.Url))
        {
            _ = OpenPageFromMenu(item);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    private void CloseCollapsedFlyout()
    {
        IsCollapsedFlyoutOpen = false;
        ActiveFlyoutChildren = null;
    }

    public async Task InitializeAsync()
    {
        ConnectionText = "Connecting...";
        await Task.CompletedTask;
    }

    private static List<MenuItemDto> BuildDefaultMenus()
    {
        var id = 1001;
        var operations = id++;
        var scheduling = id++;
        var simulation = id++;
        var intelligence = id++;
        var diagnostics = id++;

        return new List<MenuItemDto>
        {
            // ===== 运行中心：日常值班看这里 =====
            new() { Id = operations, ParentId = 0, Name = "运行中心", Icon = "▦", Description = "日常值守总入口" },
            new() { Id = id++, ParentId = operations, Name = "运行总览", Url = "/Dashboard", Icon = "⌂",
                Description = "全局概览：设备、任务、报警、追踪对象的实时数量与快捷入口。" },
            new() { Id = id++, ParentId = operations, Name = "设备状态", Url = "/Devices", Icon = "◫",
                Description = "所有输送线/PLC 设备的实时运行状态；状态变化自动刷新。" },
            new() { Id = id++, ParentId = operations, Name = "任务管理", Url = "/Tasks", Icon = "✓",
                Description = "创建、取消和跟踪搬运任务；任务状态流转实时更新，无需手动刷新。" },
            new() { Id = id++, ParentId = operations, Name = "告警中心", Url = "/Alarms", Icon = "!",
                Description = "当前活跃报警的确认与恢复；报警按防抖→风暴抑制→聚合管线产生。" },
            new() { Id = id++, ParentId = operations, Name = "对象追踪", Url = "/Objects", Icon = "◎",
                Description = "托盘/载荷在输送线上的实时位置跟踪。" },

            // ===== 调度控制：车辆与路权 =====
            new() { Id = scheduling, ParentId = 0, Name = "调度控制", Icon = "⇄", Description = "EMS/RGV 车辆调度与路权管理" },
            new() { Id = id++, ParentId = scheduling, Name = "车辆调度中心", Url = "/TransportScheduling", Icon = "⇆",
                Description = "EMS/RGV 车辆实时位置、执行中任务与路段预留（每 2 秒自动刷新）。" },
            new() { Id = id++, ParentId = scheduling, Name = "交通控制与死锁", Url = "/TransportTraffic", Icon = "◇",
                Description = "查看路权占用、等待队列和死锁检测结果；支持人工强制放行。" },
            new() { Id = id++, ParentId = scheduling, Name = "充电与优化", Url = "/TransportOptimization", Icon = "↯",
                Description = "车辆充电计划评估与调度策略参数调整。" },
            new() { Id = id++, ParentId = scheduling, Name = "生产派单看板", Url = "/TransportProduction", Icon = "▶",
                Description = "生产任务的排队、站点占用和派单决策记录；后台每秒自动派单，页面 3 秒自动刷新。" },
            new() { Id = id++, ParentId = scheduling, Name = "运行监控与一致性检查", Url = "/TransportObservability", Icon = "◉",
                Description = "调度链路健康评估、内存态与持久化数据的一致性巡检报告。" },
            new() { Id = id++, ParentId = scheduling, Name = "备份与恢复演练", Url = "/TransportResilience", Icon = "↻",
                Description = "调度状态备份、备份校验和故障恢复演练；用于验证宕机后能否安全恢复。" },
            new() { Id = id++, ParentId = scheduling, Name = "现场联调工作台", Url = "/TransportCommissioning", Icon = "⌘",
                Description = "现场调试工具：点位表校验、信号写入、冲突注入与补偿指令。" },
            new() { Id = id++, ParentId = scheduling, Name = "PLC 驱动诊断", Url = "/TransportDriverDiagnostics", Icon = "PLC",
                Description = "车辆 PLC 连接状态轮询与手动命令下发，用于排查单车通信问题。" },
            new() { Id = id++, ParentId = scheduling, Name = "配置与审计", Url = "/TransportAdministration", Icon = "⚙",
                Description = "调度参数、站点定义等敏感配置的修改审批流；所有变更留审计痕迹。" },

            // ===== 软件仿真：不接真实设备验证 =====
            new() { Id = simulation, ParentId = 0, Name = "软件仿真", Icon = "▷", Description = "无真实设备的功能验证与验收" },
            new() { Id = id++, ParentId = simulation, Name = "调度仿真报表", Url = "/TransportSimulation", Icon = "📊",
                Description = "离线跑批的历史结果：吞吐率、等待时间、车辆利用率与最终验收结论。" },
            new() { Id = id++, ParentId = simulation, Name = "统一仿真验证中心", Url = "/SimulationVerification", Icon = "S10",
                Description = "S0~S10 受治理仿真：选模板一键验收，或手工编排场景逐步执行。" },

            // ===== 工业智能：AI 分析与建议（不直接控车） =====
            new() { Id = intelligence, ParentId = 0, Name = "工业智能", Icon = "AI", Description = "AI 分析、预测与建议（只建议，不执行）" },
            new() { Id = id++, ParentId = intelligence, Name = "资产健康中心", Url = "/AssetIntelligence", Icon = "❤",
                Description = "设备健康评分、异常根因分析和剩余寿命(RUL)预测，输出维修建议。" },
            new() { Id = id++, ParentId = intelligence, Name = "IDI 总览", Url = "/IndustrialIntelligenceOverview", Icon = "IDI",
                Description = "工业决策智能各阶段能力的开放状态与环境边界一览。" },
            new() { Id = id++, ParentId = intelligence, Name = "模型管理中心", Url = "/ModelOps", Icon = "M",
                Description = "AI 模型的版本注册、审批、影子运行和回滚治理。" },
            new() { Id = id++, ParentId = intelligence, Name = "特征数据中心", Url = "/FeatureCenter", Icon = "F",
                Description = "AI 输入特征的契约定义与时间点数据集构建。" },
            new() { Id = id++, ParentId = intelligence, Name = "影子决策建议", Url = "/ShadowDecision", Icon = "◐",
                Description = "AI 生成的调度优化建议（仅展示与审批，不会直接改变现场行为）。" },
            new() { Id = id++, ParentId = intelligence, Name = "维修闭环学习", Url = "/MaintenanceLearning", Icon = "🔧",
                Description = "维修工单反馈回 AI 的闭环记录与 MES 发件箱状态。" },
            new() { Id = id++, ParentId = intelligence, Name = "数字孪生策略对比", Url = "/DigitalTwinOptimizer", Icon = "◈",
                Description = "用数字孪生批量对比不同调度策略的多目标表现，产出推荐排名。" },
            new() { Id = id++, ParentId = intelligence, Name = "自动化就绪评估", Url = "/BoundedAutomationReadiness", Icon = "🛡",
                Description = "评估有限自动化所需的软件治理证据是否齐备；生产自动控制保持关闭。" },

            // ===== 记录与审计 =====
            new() { Id = diagnostics, ParentId = 0, Name = "记录与审计", Icon = "≡", Description = "日志留痕与问题排查" },
            new() { Id = id++, ParentId = diagnostics, Name = "实时事件日志", Url = "/EventLog", Icon = "LOG",
                Description = "设备/任务/报警/物料事件的实时滚动日志（来自 SignalR 推送）。" },
        };
    }

    private async Task InitializeMenuAsync(IWcsApiService api)
    {
        try
        {
            WebResponseContent<List<MenuItemDto>>? menus = null;
            var all = BuildDefaultMenus();
            if (menus?.Data is { Count: > 0 })
                all.InsertRange(0, menus.Data);
            MenuItems = BuildMenuTree(all, 0);
        }
        catch
        {
            MenuItems = BuildMenuTree(BuildDefaultMenus(), 0);
        }
        await Task.CompletedTask;
    }

    private void OnConnectionStateChanged(bool connected) => ConnectionText = connected ? "Connected" : "Disconnected";

    private static ObservableCollection<MenuItemDto> BuildMenuTree(List<MenuItemDto> flatList, int parentId)
    {
        var tree = new ObservableCollection<MenuItemDto>();
        foreach (var item in flatList.Where(x => x.ParentId == parentId))
        {
            item.Children = BuildMenuTree(flatList, item.Id);
            tree.Add(item);
        }
        return tree;
    }

    private void SelectTab(ClosableTabItem tab)
    {
        foreach (var item in Tabs)
            item.IsSelected = ReferenceEquals(item, tab);
        SelectedTabItem = tab;
    }

    public void OpenTab(string title, object content, string? description = null)
    {
        var existing = Tabs.FirstOrDefault(x => x.Header == title);
        if (existing != null)
        {
            // 同名页签已存在：补充说明后直接聚焦
            if (!string.IsNullOrWhiteSpace(description) &&
                string.IsNullOrWhiteSpace(existing.Description))
            {
                existing.Description = description;
            }
            SelectTab(existing);
            return;
        }

        var tab = new ClosableTabItem { Header = title, Content = content, CanClose = true, Description = description };
        Tabs.Add(tab);
        SelectTab(tab);
    }

    public void CloseTab(ClosableTabItem? tab)
    {
        if (tab == null || tab.CanClose == false)
            return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // 释放页签内容：瞬态 ViewModel 订阅了单例实时服务，
        // 不 Dispose 会被事件引用链钉住造成泄漏。
        if (tab.Content is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { /* 页签关闭不应被释放异常打断 */ }
        }

        if (Tabs.Count > 0)
            SelectTab(idx > 0 ? Tabs[idx - 1] : Tabs[0]);
        else
        {
            Tabs.Add(_homeTab);
            SelectTab(_homeTab);
        }
    }

    public void Dispose()
    {
        _realtime.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    private static Type? ResolveViewModelType(string route)
    {
        var pascalRoute = string.Concat(route.Split('_', '-', '.').Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : string.Empty));
        var typeName = $"Wcs.Desktop.ViewModels.{pascalRoute}ViewModel";
        var type = Type.GetType(typeName);
        if (type != null)
            return type;

        type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.Ordinal));

        if (type == null)
            Console.WriteLine($"[MainWindowViewModel] ResolveViewModelType: type not found for route '{route}' (expected '{typeName}')");

        return type;
    }

    public async Task OpenPageFromMenu(MenuItemDto? menu)
    {
        if (menu == null || string.IsNullOrWhiteSpace(menu.Url))
            return;

        var type = ResolveViewModelType(menu.Url.TrimStart('/'));
        if (type == null)
            return;

        object content;
        try
        {
            content = _serviceProvider.GetRequiredService(type);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindowViewModel] Failed to resolve page '{menu.Url}': {ex}");
            return;
        }

        // Navigation must be immediate and must not depend on API/data initialization succeeding.
        OpenTab(menu.Name, content, string.IsNullOrWhiteSpace(menu.Description) ? null : menu.Description);

        if (content is IAsyncInitializable init)
        {
            try
            {
                await init.InitializeAsync();
            }
            catch (Exception ex)
            {
                // Keep the tab visible even when page data initialization fails. This makes
                // navigation deterministic and lets the page surface its empty/error state.
                Console.WriteLine($"[MainWindowViewModel] Page initialization failed for '{menu.Url}': {ex}");
            }
        }
    }
}

