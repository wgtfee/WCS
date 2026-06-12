using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Wcs.Desktop.Models;

namespace Wcs.Desktop.ViewModels;

public partial class NotificationCenterViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<NotificationItem> _unreadMessages = new();

    [ObservableProperty]
    private ObservableCollection<NotificationItem> _systemMessages = new();

    [ObservableProperty]
    private ObservableCollection<NotificationItem> _readMessages = new();

    [ObservableProperty]
    private int _unreadCount;

    public NotificationCenterViewModel()
    {
        LoadSampleData();
    }

    private void LoadSampleData()
    {
        UnreadMessages = new ObservableCollection<NotificationItem>
        {
            new() { Title = "设备告警", Message = "CV03 输送线故障", Time = DateTime.Now.AddMinutes(-5), Type = NotificationType.Unread },
            new() { Title = "任务完成", Message = "任务 T00042 已完成", Time = DateTime.Now.AddMinutes(-10), Type = NotificationType.Unread },
            new() { Title = "PLC 断开", Message = "PLC-01 连接已断开", Time = DateTime.Now.AddMinutes(-15), Type = NotificationType.Unread },
        };

        SystemMessages = new ObservableCollection<NotificationItem>
        {
            new() { Title = "系统维护", Message = "系统将于凌晨 2:00 进行维护", Time = DateTime.Now.AddHours(-1), Type = NotificationType.System, IsRead = true },
            new() { Title = "版本更新", Message = "WCS 已更新到 v2.1.0", Time = DateTime.Now.AddDays(-1), Type = NotificationType.System, IsRead = true },
        };

        ReadMessages = new ObservableCollection<NotificationItem>
        {
            new() { Title = "设备恢复", Message = "CV05 已恢复正常", Time = DateTime.Now.AddHours(-2), Type = NotificationType.Read, IsRead = true },
            new() { Title = "任务取消", Message = "任务 T00038 已取消", Time = DateTime.Now.AddHours(-3), Type = NotificationType.Read, IsRead = true },
        };

        UnreadCount = UnreadMessages.Count;
    }
}
