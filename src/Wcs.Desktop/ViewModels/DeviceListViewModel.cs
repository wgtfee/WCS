using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 设备列表 ViewModel — 点击刷新时从数据库加载
/// </summary>
public partial class DevicesViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public DevicesViewModel(IWcsApiService api)
    {
        _api = api;
    }

    /// <summary>
    /// 从数据库加载设备状态（Wcs_DeviceRuntime 表）
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var devices = await _api.GetDevicesFromDbAsync();
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
