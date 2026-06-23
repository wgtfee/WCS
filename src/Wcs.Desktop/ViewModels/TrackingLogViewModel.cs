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

    /// <summary>分页信息文字</summary>
    public string PageInfo => $"第 {Page} 页 / 共 {TotalPages} 页（共 {TotalCount} 条）";

    private int TotalPages => TotalCount > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;

    [RelayCommand]
    private void Search()
    {
    }

    [RelayCommand]
    private void Reset()
    {
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (Page > 1) Page--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (Page < TotalPages) Page++;
    }

    // 当分页改变时，重新切片数据
    partial void OnPageChanged(int value)
    {
        UpdatePage();
        OnPropertyChanged(nameof(PageInfo));
    }

    partial void OnPageSizeChanged(int value) => UpdatePage();
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageInfo));

    private void UpdatePage()
    {
        PagedItems.Clear();
        var items = AllItems.Skip((Page - 1) * PageSize).Take(PageSize);
        foreach (var item in items)
            PagedItems.Add(item);
    }
}
