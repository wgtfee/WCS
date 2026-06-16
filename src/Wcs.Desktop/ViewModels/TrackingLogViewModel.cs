using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Models.Log;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 埋点日志 ViewModel
/// </summary>
public partial class TrackingLogViewModel : ObservableObject
{
    [ObservableProperty]
    private string? eventName;

    [ObservableProperty]
    private string? eventType;

    [ObservableProperty]
    private string? userName;

    [ObservableProperty]
    private DateTimeOffset? beginDate;

    [ObservableProperty]
    private DateTimeOffset? endDate;

    [ObservableProperty]
    private int page = 1;

    [ObservableProperty]
    private int pageSize = 20;

    [ObservableProperty]
    private int totalCount;

    public ObservableCollection<string> EventTypes { get; } = [];

    public ObservableCollection<TrackingLogItem> PagedItems { get; } = [];

    [RelayCommand]
    private void Search()
    {
    }

    [RelayCommand]
    private void Reset()
    {
    }


}
