namespace Wcs.Desktop.Services;

/// <summary>
/// 桌面客户端配置，绑定 appsettings.json 的 WcsDesktop 节
/// </summary>
public class WcsDesktopOptions
{
    public const string SectionName = "WcsDesktop";

    public string ServerUrl { get; set; } = "http://localhost:5202";
    public string ApiPrefix { get; set; } = "/api/wcs";
    public string SignalRPath { get; set; } = "/wcs-hub";
    public int ReconnectDelaySeconds { get; set; } = 5;
    public int MaxLogEntries { get; set; } = 500;
    public int PollIntervalSeconds { get; set; } = 30;
}
