using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

public enum NotificationType
{
    Unread,
    System,
    Read
}

public partial class NotificationItem : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private DateTime _time = DateTime.Now;

    [ObservableProperty]
    private NotificationType _type = NotificationType.Unread;

    [ObservableProperty]
    private bool _isRead;
}
