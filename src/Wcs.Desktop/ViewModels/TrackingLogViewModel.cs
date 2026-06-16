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

        // 核心数据源
    public ObservableCollection<TrackingLogItem> AllItems { get; } = new();

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


   // 当分页改变时，重新切片数据
    partial void OnPageChanged(int value) => UpdatePage();
    partial void OnPageSizeChanged(int value) => UpdatePage();

    private void UpdatePage()
    {
        PagedItems.Clear();
        var items = AllItems.Skip((Page - 1) * PageSize).Take(PageSize);
        foreach (var item in items)
            PagedItems.Add(item);
    }

}
