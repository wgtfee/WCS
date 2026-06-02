namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.Persistence.Repositories;

/// <summary>
/// Infrastructure 层 DI 注册
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddWcsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("WcsDb")
            ?? throw new InvalidOperationException(
                "连接字符串 'WcsDb' 未配置。请在 appsettings.json 中设置 ConnectionStrings:WcsDb。");

        // 数据库初始化器
        services.AddSingleton<IDatabaseInitializer>(sp =>
            new DatabaseInitializer(
                connectionString,
                sp.GetRequiredService<ILogger<DatabaseInitializer>>()));

        // Dapper 仓库
        services.AddSingleton<TaskRepository>(_ => new TaskRepository(connectionString));
        services.AddSingleton<AlarmRepository>(_ => new AlarmRepository(connectionString));
        services.AddSingleton<TaskEventRepository>(_ => new TaskEventRepository(connectionString));

        return services;
    }
}
