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
using Wcs.Service;

namespace Wcs.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncInitializable
{
    private readonly IWcsRealtimeService _realtime;
    private readonly IServiceProvider _serviceProvider;

   private readonly LoadService _loadService;

    [ObservableProperty]
    private string _connectionText = "Disconnected";

    [ObservableProperty]
    private ClosableTabItem? _selectedTabItem;

    [ObservableProperty]
    private ObservableCollection<MenuItemDto> _menuItems = new();

    private NotificationCenterViewModel NotificationCenter { get; } = new();

    public ObservableCollection<ClosableTabItem> Tabs { get; } = new();
    private readonly IDataProvider _dataProvider;
    private ClosableTabItem _homeTab;

    public MainWindowViewModel(
        IWcsRealtimeService realtime,
        IWcsApiService api,
        IOptions<WcsDesktopOptions> options,
        IServiceProvider serviceProvider,
        LoadService loadService,
        DashboardViewModel dashboard,IDataProvider dataprovider)
    {
        _realtime = realtime;
        _serviceProvider = serviceProvider;
        _loadService = loadService;
        _homeTab = new ClosableTabItem { Header = "Dashboard", Content = dashboard, CanClose = false, IsSelected = true };
        Tabs.Add(_homeTab);
        SelectedTabItem = _homeTab;
        _dataProvider = dataprovider;
        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
        _ = InitializeMenuAsync(api);
    }

    public async Task InitializeAsync()
    {
        ConnectionText = "Connecting...";
        await Task.CompletedTask;
    }

    /// <summary>构建基础菜单（6 个默认页面），Id 偏移 1000 避免与 API 菜单冲突</summary>
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

    public void OpenTab(string title, object content)
    {
        var existing = Tabs.FirstOrDefault(x => x.Header == title);
        if (existing != null) { SelectedTabItem = existing; return; }

        var tab = new ClosableTabItem { Header = title, Content = content, CanClose = true };
        Tabs.Add(tab);
        SelectedTabItem = tab;
    }

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

    public async Task OpenPageFromMenu(MenuItemDto? menu)
    {
        if (menu == null || string.IsNullOrWhiteSpace(menu.Url)) return;

        object? content = menu.Url switch
        {
            "/Dashboard" => (object?)_serviceProvider.GetRequiredService<DashboardViewModel>(),
            "/Devices"   => _serviceProvider.GetRequiredService<DeviceListViewModel>(),
            "/Tasks"     => _serviceProvider.GetRequiredService<TaskManagementViewModel>(),
            "/Alarms"    => _serviceProvider.GetRequiredService<AlarmPanelViewModel>(),
            "/Objects"   => _serviceProvider.GetRequiredService<ObjectTrackingViewModel>(),
            "/EventLog"  => _serviceProvider.GetRequiredService<EventLogViewModel>(),
            _ => null
        };
        if (content == null) return;
        if (content is IAsyncInitializable init) await init.InitializeAsync();
        OpenTab(menu.Name, content);
    }
}
