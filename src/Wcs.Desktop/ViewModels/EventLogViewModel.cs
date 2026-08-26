using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 实时事件日志 ViewModel
/// </summary>
public partial class EventLogViewModel : ViewModelBase
{
    private readonly IWcsRealtimeService _realtime;
    private int _maxEntries = 500;

    // 保存委托引用以便退订（lambda 直接订阅会因引用不同而无法 -=）
    private readonly Action<bool> _onConnectionChanged;
    private readonly Action<DeviceStateChangedMessage> _onDeviceStateChanged;
    private readonly Action<DeviceStateChangedMessage> _onDeviceStateBroadcast;
    private readonly Action<TaskStateChangedMessage> _onTaskStateChanged;
    private readonly Action<AlarmEventMessage> _onAlarmEvent;
    private readonly Action<AlarmEventMessage> _onAlarmBroadcast;
    private readonly Action<ObjectMovedMessage> _onObjectMoved;

    public ObservableCollection<EventLogEntry> Entries { get; } = new();

    [ObservableProperty] private bool _autoScroll = true;

    public EventLogViewModel(IWcsRealtimeService realtime)
    {
        _realtime = realtime;

        _onConnectionChanged = connected =>
            AddEntry("System", connected ? "Connected to server" : "Disconnected from server",
                connected ? "Info" : "Warning");

        _onDeviceStateChanged = msg =>
            AddEntry("Device", $"Device '{msg.DeviceId}' state → {msg.State.Status}");

        _onDeviceStateBroadcast = msg =>
            AddEntry("Device", $"Device '{msg.DeviceId}' broadcast: {msg.State.Status}");

        _onTaskStateChanged = msg =>
            AddEntry("Task", $"Task '{msg.TaskId}' state → {msg.Runtime.Status}");

        _onAlarmEvent = msg =>
            AddEntry("Alarm", $"[{msg.Action}] Alarm event received",
                msg.Action == "Raised" ? "Warning" : "Info");

        _onAlarmBroadcast = msg =>
            AddEntry("Alarm", $"[{msg.Action}] Alarm broadcast: {msg.Alarm}");

        _onObjectMoved = msg =>
            AddEntry("Object", $"Object '{msg.ObjectId}': {msg.OldPos} → {msg.NewPos}");

        _realtime.ConnectionStateChanged += _onConnectionChanged;
        _realtime.DeviceStateChanged += _onDeviceStateChanged;
        _realtime.DeviceStateBroadcast += _onDeviceStateBroadcast;
        _realtime.TaskStateChanged += _onTaskStateChanged;
        _realtime.AlarmEvent += _onAlarmEvent;
        _realtime.AlarmBroadcast += _onAlarmBroadcast;
        _realtime.ObjectMoved += _onObjectMoved;
    }

    private void AddEntry(string category, string message, string? level = null)
    {
        var entry = new EventLogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Message = message,
            Level = level
        };

        if (Entries.Count >= _maxEntries)
            Entries.RemoveAt(Entries.Count - 1);

        Entries.Insert(0, entry);
    }

    protected override void OnDispose()
    {
        _realtime.ConnectionStateChanged -= _onConnectionChanged;
        _realtime.DeviceStateChanged -= _onDeviceStateChanged;
        _realtime.DeviceStateBroadcast -= _onDeviceStateBroadcast;
        _realtime.TaskStateChanged -= _onTaskStateChanged;
        _realtime.AlarmEvent -= _onAlarmEvent;
        _realtime.AlarmBroadcast -= _onAlarmBroadcast;
        _realtime.ObjectMoved -= _onObjectMoved;
        base.OnDispose();
    }
}
