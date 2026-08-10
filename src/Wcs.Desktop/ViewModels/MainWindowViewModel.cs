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
    private readonly WcsDesktopOptions _options;
    private readonly ClosableTabItem _homeTab;

    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private ClosableTabItem? _selectedTabItem;
    [ObservableProperty] private ObservableCollection<MenuItemDto> _menuItems = new();
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private MenuItemDto? _selectedMenuItem;
    [ObservableProperty] private bool _isCollapsedFlyoutOpen;
    [ObservableProperty] private ObservableCollection<MenuItemDto>? _activeFlyoutChildren;
    [ObservableProperty] private MenuItemDto? _activeFlyoutSelectedItem;

    public double SidebarWidth => IsSidebarCollapsed ? 48 : 240;
    public NotificationCenterViewModel NotificationCenter { get; } = new();
    public ObservableCollection<ClosableTabItem> Tabs { get; } = new();

    public MainWindowViewModel(IWcsRealtimeService realtime, IWcsApiService api, IOptions<WcsDesktopOptions> options, IServiceProvider serviceProvider, DashboardViewModel dashboard, IDataProvider dataprovider)
    {
        _realtime = realtime;
        _serviceProvider = serviceProvider;
        _dataProvider = dataprovider;
        _options = options.Value;
        _homeTab = new ClosableTabItem { Header = "Dashboard", Content = dashboard, CanClose = false, IsSelected = true };
        Tabs.Add(_homeTab);
        SelectedTabItem = _homeTab;
        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
        _ = InitializeMenuAsync(api);
    }

    partial void OnIsSidebarCollapsedChanged(bool value) { OnPropertyChanged(nameof(SidebarWidth)); if (!value) CloseCollapsedFlyout(); }
    partial void OnSelectedMenuItemChanged(MenuItemDto? value) { if (value != null && (value.Children == null || value.Children.Count == 0)) { _ = OpenPageFromMenu(value); SelectedMenuItem = null; } }
    partial void OnActiveFlyoutSelectedItemChanged(MenuItemDto? value) { if (value == null) return; if (value.Children.Count > 0) ActiveFlyoutChildren = value.Children; else { _ = OpenPageFromMenu(value); CloseCollapsedFlyout(); } ActiveFlyoutSelectedItem = null; }

    [RelayCommand] private void CollapsedMenuClick(MenuItemDto item) { if (item.Children.Count > 0) { ActiveFlyoutChildren = item.Children; IsCollapsedFlyoutOpen = true; } else if (!string.IsNullOrEmpty(item.Url)) _ = OpenPageFromMenu(item); }
    [RelayCommand] private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;
    private void CloseCollapsedFlyout() { IsCollapsedFlyoutOpen = false; ActiveFlyoutChildren = null; }

    public async Task InitializeAsync()
    {
        ConnectionText = "Connecting...";
        try
        {
            await _realtime.ConnectAsync(_options.ServerUrl);
        }
        catch
        {
            ConnectionText = "Disconnected";
            throw;
        }
    }

    private static List<MenuItemDto> BuildDefaultMenus()
    {
        var id = 1001;
        return new List<MenuItemDto>
        {
            new() { Id = id++, ParentId = 0, Name = "Dashboard", Url = "/Dashboard" },
            new() { Id = id++, ParentId = 0, Name = "Devices", Url = "/Devices" },
            new() { Id = id++, ParentId = 0, Name = "Tasks", Url = "/Tasks" },
            new() { Id = id++, ParentId = 0, Name = "EMS / RGV 调度", Url = "/TransportScheduling" },
            new() { Id = id++, ParentId = 0, Name = "交通控制与死锁", Url = "/TransportTraffic" },
            new() { Id = id++, ParentId = 0, Name = "充电与运行优化", Url = "/TransportOptimization" },
            new() { Id = id++, ParentId = 0, Name = "生产级调度", Url = "/TransportProduction" },
            new() { Id = id++, ParentId = 0, Name = "可观测性与一致性", Url = "/TransportObservability" },
            new() { Id = id++, ParentId = 0, Name = "生产韧性与恢复演练", Url = "/TransportResilience" },
            new() { Id = id++, ParentId = 0, Name = "调度仿真与最终验收", Url = "/TransportSimulation" },
            new() { Id = id++, ParentId = 0, Name = "配置与审计", Url = "/TransportAdministration" },
            new() { Id = id++, ParentId = 0, Name = "PLC 驱动诊断", Url = "/TransportDriverDiagnostics" },
            new() { Id = id++, ParentId = 0, Name = "现场联调工作台", Url = "/TransportCommissioning" },
            new() { Id = id++, ParentId = 0, Name = "Alarms", Url = "/Alarms" },
            new() { Id = id++, ParentId = 0, Name = "Objects", Url = "/Objects" },
            new() { Id = id++, ParentId = 0, Name = "Event Log", Url = "/EventLog" },
        };
    }

    private async Task InitializeMenuAsync(IWcsApiService api) { try { WebResponseContent<List<MenuItemDto>>? menus = null; var all = BuildDefaultMenus(); if (menus?.Data is { Count: > 0 }) all.InsertRange(0, menus.Data); MenuItems = BuildMenuTree(all, 0); } catch { MenuItems = BuildMenuTree(BuildDefaultMenus(), 0); } await Task.CompletedTask; }
    private void OnConnectionStateChanged(bool connected) => ConnectionText = connected ? "Connected" : "Disconnected";

    private static ObservableCollection<MenuItemDto> BuildMenuTree(List<MenuItemDto> flatList, int parentId)
    {
        var tree = new ObservableCollection<MenuItemDto>();
        foreach (var item in flatList.Where(x => x.ParentId == parentId)) { item.Children = BuildMenuTree(flatList, item.Id); tree.Add(item); }
        return tree;
    }

    public void OpenTab(string title, object content) { var existing = Tabs.FirstOrDefault(x => x.Header == title); if (existing != null) { SelectedTabItem = existing; return; } var tab = new ClosableTabItem { Header = title, Content = content, CanClose = true }; Tabs.Add(tab); SelectedTabItem = tab; }
    public void CloseTab(ClosableTabItem? tab) { if (tab == null || tab.CanClose == false) return; var idx = Tabs.IndexOf(tab); Tabs.Remove(tab); if (Tabs.Count > 0) SelectedTabItem = idx > 0 ? Tabs[idx - 1] : Tabs[0]; else { Tabs.Add(_homeTab); SelectedTabItem = _homeTab; } }

    private static Type? ResolveViewModelType(string route) { var pascalRoute = string.Concat(route.Split('_', '-', '.').Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : string.Empty)); return Type.GetType($"Wcs.Desktop.ViewModels.{pascalRoute}ViewModel"); }

    public async Task OpenPageFromMenu(MenuItemDto? menu)
    {
        if (menu == null || string.IsNullOrWhiteSpace(menu.Url)) return;
        var type = ResolveViewModelType(menu.Url.TrimStart('/'));
        if (type == null) return;
        var content = _serviceProvider.GetRequiredService(type);
        if (content is IAsyncInitializable init) await init.InitializeAsync();
        OpenTab(menu.Name, content);
    }
}
