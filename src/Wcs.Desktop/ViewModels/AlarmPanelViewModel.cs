using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 报警面板 ViewModel — 点击刷新时从数据库加载，支持按列搜索过滤
/// </summary>
public partial class AlarmsViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private List<AlarmItem> _allAlarms = new();

    public ObservableCollection<AlarmItem> Alarms { get; } = new();
    public List<string> SearchFields { get; } = new()
    {
        "全部", "报警 ID", "报警码", "级别", "消息", "状态"
    };

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchField = "全部";

    public AlarmsViewModel(IWcsApiService api)
    {
        _api = api;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSearchFieldChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Alarms.Clear();
        var items = string.IsNullOrWhiteSpace(SearchText)
            ? _allAlarms
            : _allAlarms.Where(a => SearchField switch
            {
                "报警 ID" => a.AlarmId.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "报警码" => a.AlarmCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "级别" => a.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "消息" => a.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "状态" => a.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                _ => a.AlarmId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     a.AlarmCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     a.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     a.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     a.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            });
        foreach (var item in items)
            Alarms.Add(item);
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var alarms = await _api.GetAlarmsFromDbAsync();
            _allAlarms = alarms.Select(a => new AlarmItem
            {
                AlarmId = a.AlarmId,
                AlarmCode = a.AlarmCode,
                Status = a.Status.ToString(),
                Level = a.Level.ToString(),
                Message = a.Message,
                OccurTime = a.OccurTime,
                RecoverTime = a.RecoverTime
            }).ToList();
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task AckAlarmAsync(string? alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId)) return;
        try
        {
            await _api.AckAlarmAsync(alarmId);
            var item = Alarms.FirstOrDefault(a => a.AlarmCode == alarmId);
            if (item is not null) item.Status = "Acknowledged";
        }
        catch { }
    }

    [RelayCommand]
    public async Task RecoverAlarmAsync(string? alarmCode)
    {
        if (string.IsNullOrWhiteSpace(alarmCode)) return;
        try
        {
            await _api.RecoverAlarmAsync(alarmCode);
            var item = Alarms.FirstOrDefault(a => a.AlarmCode == alarmCode);
            if (item is not null) Alarms.Remove(item);
        }
        catch { }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();
}
