namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Core.Persistence;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.Persistence.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddWcsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException(
                "连接字符串 'WcsDb' 未配置。请在 appsettings.json 中设置 ConnectionStrings:WcsDb。");

        // 数据库初始化器（SqlSugar CodeFirst）
        services.AddSingleton<IDatabaseInitializer>(sp =>
            new DatabaseInitializer(
                connectionString,
                sp.GetRequiredService<ILogger<DatabaseInitializer>>()));

        // 数据库查询服务（依赖 ISqlSugarClient，在 Host Program.cs 中注册）
        services.AddSingleton<IAlarmQueryService, AlarmQueryService>();
        services.AddSingleton<ITaskQueryService, TaskQueryService>();
        services.AddSingleton<IDeviceQueryService, DeviceQueryService>();

        return services;
    }
}
