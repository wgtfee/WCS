using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 实时事件日志 ViewModel
/// </summary>
public partial class EventLogViewModel : ObservableObject
{
    private readonly IWcsRealtimeService _realtime;
    private int _maxEntries = 500;

    public ObservableCollection<EventLogEntry> Entries { get; } = new();

    [ObservableProperty] private bool _autoScroll = true;

    public EventLogViewModel(IWcsRealtimeService realtime)
    {
        _realtime = realtime;

        _realtime.ConnectionStateChanged += connected =>
            AddEntry("System", connected ? "Connected to server" : "Disconnected from server",
                connected ? "Info" : "Warning");

        _realtime.DeviceStateChanged += msg =>
            AddEntry("Device", $"Device '{msg.DeviceId}' state → {msg.State.Status}");

        _realtime.DeviceStateBroadcast += msg =>
            AddEntry("Device", $"Device '{msg.DeviceId}' broadcast: {msg.State.Status}");

        _realtime.TaskStateChanged += msg =>
            AddEntry("Task", $"Task '{msg.TaskId}' state → {msg.Runtime.Status}");

        _realtime.AlarmEvent += msg =>
            AddEntry("Alarm", $"[{msg.Action}] Alarm event received",
                msg.Action == "Raised" ? "Warning" : "Info");

        _realtime.AlarmBroadcast += msg =>
            AddEntry("Alarm", $"[{msg.Action}] Alarm broadcast: {msg.Alarm}");

        _realtime.ObjectMoved += msg =>
            AddEntry("Object", $"Object '{msg.ObjectId}': {msg.OldPos} → {msg.NewPos}");
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
}
