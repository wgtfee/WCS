using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 报警面板 ViewModel
/// </summary>
public partial class AlarmPanelViewModel : ObservableObject
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    public ObservableCollection<AlarmItem> Alarms { get; } = new();

    [ObservableProperty] private bool _isLoading;

    public AlarmPanelViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.AlarmBroadcast += msg =>
        {
            if (msg.Action == "Raised" && msg.Alarm is Wcs.Core.StateCenter.Models.AlarmState alarm)
            {
                Alarms.Insert(0, new AlarmItem
                {
                    AlarmId = alarm.AlarmId,
                    AlarmCode = alarm.AlarmCode,
                    Status = alarm.Status.ToString(),
                    Level = alarm.Level.ToString(),
                    Message = alarm.Message,
                    OccurTime = alarm.OccurTime
                });
            }
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var alarms = await _api.GetAlarmsAsync();
            Alarms.Clear();
            foreach (var a in alarms)
            {
                Alarms.Add(new AlarmItem
                {
                    AlarmId = a.AlarmId,
                    AlarmCode = a.AlarmCode,
                    Status = a.Status.ToString(),
                    Level = a.Level.ToString(),
                    Message = a.Message,
                    OccurTime = a.OccurTime,
                    RecoverTime = a.RecoverTime
                });
            }
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
