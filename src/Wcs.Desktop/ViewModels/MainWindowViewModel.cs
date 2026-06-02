using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// Tab 页包装
/// </summary>
public class TabItem
{
    public string Header { get; init; } = string.Empty;
    public object Content { get; init; } = null!;
}

/// <summary>
/// 主窗口 ViewModel - Tab 导航宿主
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IWcsRealtimeService _realtime;
    private readonly IWcsApiService _api;
    private readonly IOptions<WcsDesktopOptions> _options;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "Disconnected";
    [ObservableProperty] private int _selectedTabIndex;

    public ObservableCollection<TabItem> Tabs { get; } = new();

    public MainWindowViewModel(
        IWcsRealtimeService realtime,
        IWcsApiService api,
        IOptions<WcsDesktopOptions> options,
        DashboardViewModel dashboard,
        DeviceListViewModel deviceList,
        TaskManagementViewModel taskManagement,
        AlarmPanelViewModel alarmPanel,
        ObjectTrackingViewModel objectTracking,
        EventLogViewModel eventLog)
    {
        _realtime = realtime;
        _api = api;
        _options = options;

        Tabs = new ObservableCollection<TabItem>
        {
            new() { Header = "Dashboard", Content = dashboard },
            new() { Header = "Devices", Content = deviceList },
            new() { Header = "Tasks", Content = taskManagement },
            new() { Header = "Alarms", Content = alarmPanel },
            new() { Header = "Objects", Content = objectTracking },
            new() { Header = "Event Log", Content = eventLog }
        };

        _realtime.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public async Task InitializeAsync()
    {
        var serverUrl = _options.Value.ServerUrl;
        ConnectionText = "Connecting...";

        try
        {
            await _realtime.ConnectAsync(serverUrl);

            // Initial data load for all tabs
            await Task.WhenAll(
                ((DashboardViewModel)Tabs[0].Content).LoadAsync(),
                ((DeviceListViewModel)Tabs[1].Content).LoadAsync(),
                ((TaskManagementViewModel)Tabs[2].Content).LoadAsync(),
                ((AlarmPanelViewModel)Tabs[3].Content).LoadAsync(),
                ((ObjectTrackingViewModel)Tabs[4].Content).LoadAsync()
            );
        }
        catch (Exception ex)
        {
            ConnectionText = $"Connection failed: {ex.Message}";
        }
    }

    private void OnConnectionStateChanged(bool connected)
    {
        IsConnected = connected;
        ConnectionText = connected ? "Connected" : "Disconnected";
    }
}
