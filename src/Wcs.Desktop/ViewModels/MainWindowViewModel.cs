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

    [ObservableProperty]
    private string _connectionText = "Disconnected";

    [ObservableProperty]
    private ClosableTabItem? _selectedTabItem;

    [ObservableProperty]
    private ObservableCollection<MenuItemDto> _menuItems = new();

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private MenuItemDto? _selectedMenuItem;

    // -- 折叠模式弹出菜单状态 --
    [ObservableProperty]
    private bool _isCollapsedFlyoutOpen;

    [ObservableProperty]
    private ObservableCollection<MenuItemDto>? _activeFlyoutChildren;

    [ObservableProperty]
    private MenuItemDto? _activeFlyoutSelectedItem;

    public double SidebarWidth => IsSidebarCollapsed ? 48 : 240;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        if (!value) CloseCollapsedFlyout(); // 展开时关闭弹出
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
        if (value == null) return;

        if (value.Children.Count > 0)
        {
            // 有子菜单 → 切换到下一级
            ActiveFlyoutChildren = value.Children;
        }
        else
        {
            // 叶子节点 → 导航并关闭
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

    private void CloseCollapsedFlyout()
    {
        IsCollapsedFlyoutOpen = false;
        ActiveFlyoutChildren = null;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    public NotificationCenterViewModel NotificationCenter { get; } = new();

    public ObservableCollection<ClosableTabItem> Tabs { get; } = new();
    private readonly IDataProvider _dataProvider;
    private ClosableTabItem _homeTab;

    public MainWindowViewModel(
        IWcsRealtimeService realtime,
        IWcsApiService api,
        IOptions<WcsDesktopOptions> options,
        IServiceProvider serviceProvider,
        DashboardViewModel dashboard,IDataProvider dataprovider)
    {
        _realtime = realtime;
        _serviceProvider = serviceProvider;
        _homeTab = new ClosableTabItem { Header = "Dashboard", Content = dashboard, CanClose = false, IsSelected = true };
        Tabs.Add(_homeTab);
        SelectedTabItem = _homeTab;
        _dataProvider = dataprovider;
        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
        _ = InitializeMenuAsync(api);
    }
    
    /// <summary>
    /// 应用启动时的异步初始化方法，当前仅设置连接状态文本，后续可扩展为加载用户信息、权限等
    /// </summary>
    /// <returns></returns>
    public async Task InitializeAsync()
    {
        ConnectionText = "Connecting...";
        await Task.CompletedTask;
    }

    /// <summary>构建基础菜单（6 个默认页面 + 补充新增日志页面），Id 偏移 1000 避免与 API 菜单冲突</summary>
    private static List<MenuItemDto> BuildDefaultMenus()
    {
        int id = 1001;
        return new List<MenuItemDto>
        {
            new() { Id = id++, ParentId = 0, Name = "Dashboard", Url = "/Dashboard" },
            new() { Id = id++, ParentId = 0, Name = "Devices",   Url = "/Devices" },
            new() { Id = id++, ParentId = 0, Name = "Tasks",     Url = "/Tasks" },
            new() { Id = id++, ParentId = 0, Name = "Alarms",    Url = "/Alarms" },
            new() { Id = id++, ParentId = 0, Name = "Objects",   Url = "/Objects" },
            new() { Id = id++, ParentId = 0, Name = "Event Log", Url = "/EventLog" },
        };
    }
    
    /// <summary>
    /// 从 API 获取菜单数据并构建菜单树，若失败则使用默认菜单
    /// </summary>
    /// <param name="api"></param>
    /// <returns></returns>
    private async Task InitializeMenuAsync(IWcsApiService api)
    {
        try
        {
            WebResponseContent<List<MenuItemDto>> menus = null;
            try
            {
                menus = await _dataProvider.GetMenus(UserInfo.User?.RoleId ?? 1);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Menu] API exception: {ex.Message}");
            }

            // 合并：默认菜单在后，API 动态菜单在前
            var all = BuildDefaultMenus();
            if (menus != null && menus.Data != null && menus.Data.Count > 0)
                all.InsertRange(0, menus.Data);
            MenuItems = BuildMenuTree(all, 0);
        }
        catch
        {
            MenuItems = BuildMenuTree(BuildDefaultMenus(), 0);
        }
    }

    /// <summary>
    /// 连接状态变化回调，更新 UI 显示
    /// </summary>
    /// <param name="connected"></param>
    private void OnConnectionStateChanged(bool connected)
        => ConnectionText = connected ? "Connected" : "Disconnected";

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
    
    /// <summary>
    /// 打开一个新的标签页，若已存在同名标签则切换到该标签
    /// </summary>
    /// <param name="title"></param>
    /// <param name="content"></param>
    public void OpenTab(string title, object content)
    {
        var existing = Tabs.FirstOrDefault(x => x.Header == title);
        if (existing != null) { SelectedTabItem = existing; return; }

        var tab = new ClosableTabItem { Header = title, Content = content, CanClose = true };
        Tabs.Add(tab);
        SelectedTabItem = tab;
    }
    
    /// <summary>
    /// 关闭指定标签页，若无剩余标签则回到主页
    /// </summary>
    /// <param name="tab"></param>
    public void CloseTab(ClosableTabItem? tab)
    {
        if (tab == null || tab.CanClose == false) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count > 0)
            SelectedTabItem = idx > 0 ? Tabs[idx - 1] : Tabs[0];
        else
        {
            Tabs.Add(_homeTab);
            SelectedTabItem = _homeTab;
        }
    }

    /// <summary>根据路由名自动匹配 ViewModel，约定：{Route}ViewModel</summary>
    private static Type? ResolveViewModelType(string route)
    {
        // 将下划线命名转为帕斯卡（如 "Sys_Log" → "SysLog"）
        var pascalRoute = string.Concat(
            route.Split('_', '-', '.')
                .Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : ""));
        var typeName = $"Wcs.Desktop.ViewModels.{pascalRoute}ViewModel";
        return Type.GetType(typeName);
    }

    /// <summary>
    /// 根据菜单项打开对应页面，约定：菜单 URL 对应 ViewModel 路由（如 "/Devices" → DevicesViewModel）
    /// </summary>
    /// <param name="menu"></param>
    /// <returns></returns>
    public async Task OpenPageFromMenu(MenuItemDto? menu)
    {
        if (menu == null || string.IsNullOrWhiteSpace(menu.Url)) return;

        var route = menu.Url.TrimStart('/');
        var type = ResolveViewModelType(route);
        if (type == null) return;

        var content = _serviceProvider.GetRequiredService(type);
        if (content is IAsyncInitializable init) await init.InitializeAsync();
        OpenTab(menu.Name, content);
    }
}
