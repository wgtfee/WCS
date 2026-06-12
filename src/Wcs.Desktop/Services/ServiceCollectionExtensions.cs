using Microsoft.Extensions.DependencyInjection;
using Wcs.Desktop.Interface;
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

        // 数据提供者
        services.AddSingleton<Wcs.Desktop.Interface.IDataProvider, DataProvider>();

        // 主题服务
        services.AddSingleton<ThemeService>();

        // ViewModels
        services.AddTransient<LoginViewModel>();
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
