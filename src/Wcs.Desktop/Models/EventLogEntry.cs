namespace Wcs.Desktop.Models;

/// <summary>
/// 事件日志条目
/// </summary>
public class EventLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Level { get; init; }
}
