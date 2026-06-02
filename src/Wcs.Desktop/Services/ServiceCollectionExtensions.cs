using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Wcs.Desktop.Services;
using Wcs.Desktop.ViewModels;

namespace Wcs.Desktop;

/// <summary>
/// Wcs.Desktop DI 注册扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWcsDesktop(this IServiceCollection services)
    {
        // 配置
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.Configure<WcsDesktopOptions>(config.GetSection("WcsDesktop"));

        // HTTP 客户端
        services.AddHttpClient<IWcsApiService, WcsApiService>();

        // SignalR 实时服务
        services.AddSingleton<IWcsRealtimeService, WcsRealtimeService>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<DeviceListViewModel>();
        services.AddTransient<TaskManagementViewModel>();
        services.AddTransient<AlarmPanelViewModel>();
        services.AddTransient<ObjectTrackingViewModel>();
        services.AddTransient<EventLogViewModel>();

        return services;
    }
}
