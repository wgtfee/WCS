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
        _homeTab = new ClosableTabItem { Header = "运行总览", Content = dashboard, CanClose = false, IsSelected = true };
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
            new() { Id = operations, ParentId = 0, Name = "运行中心", Icon = "▦" },
            new() { Id = id++, ParentId = operations, Name = "运行总览", Url = "/Dashboard", Icon = "⌂" },
            new() { Id = id++, ParentId = operations, Name = "设备状态", Url = "/Devices", Icon = "◫" },
            new() { Id = id++, ParentId = operations, Name = "任务管理", Url = "/Tasks", Icon = "✓" },
            new() { Id = id++, ParentId = operations, Name = "告警中心", Url = "/Alarms", Icon = "!" },
            new() { Id = id++, ParentId = operations, Name = "对象追踪", Url = "/Objects", Icon = "◎" },

            new() { Id = scheduling, ParentId = 0, Name = "调度控制", Icon = "⇄" },
            new() { Id = id++, ParentId = scheduling, Name = "EMS / RGV 调度", Url = "/TransportScheduling", Icon = "⇆" },
            new() { Id = id++, ParentId = scheduling, Name = "交通控制与死锁", Url = "/TransportTraffic", Icon = "◇" },
            new() { Id = id++, ParentId = scheduling, Name = "充电与运行优化", Url = "/TransportOptimization", Icon = "↯" },
            new() { Id = id++, ParentId = scheduling, Name = "生产级调度", Url = "/TransportProduction", Icon = "▶" },
            new() { Id = id++, ParentId = scheduling, Name = "可观测性与一致性", Url = "/TransportObservability", Icon = "◉" },
            new() { Id = id++, ParentId = scheduling, Name = "生产韧性与恢复演练", Url = "/TransportResilience", Icon = "↻" },
            new() { Id = id++, ParentId = scheduling, Name = "现场联调工作台", Url = "/TransportCommissioning", Icon = "⌘" },
            new() { Id = id++, ParentId = scheduling, Name = "PLC 驱动诊断", Url = "/TransportDriverDiagnostics", Icon = "PLC" },
            new() { Id = id++, ParentId = scheduling, Name = "配置与审计", Url = "/TransportAdministration", Icon = "⚙" },

            new() { Id = simulation, ParentId = 0, Name = "软件仿真", Icon = "▷" },
            new() { Id = id++, ParentId = simulation, Name = "调度仿真与最终验收", Url = "/TransportSimulation", Icon = "▶" },
            new() { Id = id++, ParentId = simulation, Name = "统一仿真验证中心", Url = "/SimulationVerification", Icon = "S10" },

            new() { Id = intelligence, ParentId = 0, Name = "工业智能", Icon = "AI" },
            new() { Id = id++, ParentId = intelligence, Name = "智能运维中心", Url = "/AssetIntelligence", Icon = "◇" },
            new() { Id = id++, ParentId = intelligence, Name = "IDI 总览", Url = "/IndustrialIntelligenceOverview", Icon = "IDI" },
            new() { Id = id++, ParentId = intelligence, Name = "P1 ModelOps Center", Url = "/ModelOps", Icon = "P1" },
            new() { Id = id++, ParentId = intelligence, Name = "P2 Feature Center", Url = "/FeatureCenter", Icon = "P2" },
            new() { Id = id++, ParentId = intelligence, Name = "P3 Shadow Decision", Url = "/ShadowDecision", Icon = "P3" },
            new() { Id = id++, ParentId = intelligence, Name = "P4 Maintenance Learning", Url = "/MaintenanceLearning", Icon = "P4" },
            new() { Id = id++, ParentId = intelligence, Name = "P5 Digital Twin Optimizer", Url = "/DigitalTwinOptimizer", Icon = "P5" },
            new() { Id = id++, ParentId = intelligence, Name = "P6 Automation Readiness", Url = "/BoundedAutomationReadiness", Icon = "P6" },

            new() { Id = diagnostics, ParentId = 0, Name = "记录与审计", Icon = "≡" },
            new() { Id = id++, ParentId = diagnostics, Name = "事件日志", Url = "/EventLog", Icon = "LOG" },
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

    public void OpenTab(string title, object content)
    {
        var existing = Tabs.FirstOrDefault(x => x.Header == title);
        if (existing != null)
        {
            SelectTab(existing);
            return;
        }

        var tab = new ClosableTabItem { Header = title, Content = content, CanClose = true };
        Tabs.Add(tab);
        SelectTab(tab);
    }

    public void CloseTab(ClosableTabItem? tab)
    {
        if (tab == null || tab.CanClose == false)
            return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count > 0)
            SelectTab(idx > 0 ? Tabs[idx - 1] : Tabs[0]);
        else
        {
            Tabs.Add(_homeTab);
            SelectTab(_homeTab);
        }
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
        OpenTab(menu.Name, content);

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
