namespace Wcs.Infrastructure.Logging;

using Serilog;
using Serilog.Events;

/// <summary>
/// Serilog 日志配置
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// 配置 Serilog
    /// </summary>
    public static ILogger CreateLogger(string? logPath = null)
    {
        logPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "wcs-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
