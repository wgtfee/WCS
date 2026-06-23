using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 报警面板 ViewModel — 点击刷新时从数据库加载，支持本地搜索过滤
/// </summary>
public partial class AlarmsViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private List<AlarmItem> _allAlarms = new();

    public ObservableCollection<AlarmItem> Alarms { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;

    public AlarmsViewModel(IWcsApiService api)
    {
        _api = api;
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter(value);
    }

    private void ApplyFilter(string? filter)
    {
        Alarms.Clear();
        var items = string.IsNullOrWhiteSpace(filter)
            ? _allAlarms
            : _allAlarms.Where(a =>
                a.AlarmId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.AlarmCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Level.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Status.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var item in items)
            Alarms.Add(item);
    }

    /// <summary>
    /// 从数据库加载报警状态（Wcs_AlarmRuntime 表）
    /// </summary>
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
            ApplyFilter(SearchText);
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
            var item = Alarms.FirstOrDefault(a => a.AlarmId == alarmId);
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
