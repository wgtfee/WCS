using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 设备列表 ViewModel
/// </summary>
public partial class DevicesViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public DevicesViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.DeviceStateBroadcast += msg =>
        {
            var existing = Devices.FirstOrDefault(d => d.DeviceId == msg.DeviceId);
            if (existing is not null)
            {
                existing.Status = msg.State.Status.ToString();
                existing.LastUpdateTime = msg.State.LastUpdateTime;
                existing.CurrentPosition = msg.State.CurrentPosition;
            }
        };
    }

    protected override async Task OnInitializeAsync()
    {
        await LoadAsync();
    }
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var devices = await _api.GetDevicesAsync();
            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new DeviceItem
                {
                    DeviceId = d.DeviceId,
                    Status = d.Status.ToString(),
                    LastUpdateTime = d.LastUpdateTime,
                    CurrentPosition = d.CurrentPosition
                });
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();
}
