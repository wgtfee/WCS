namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wcs.Core.Persistence;
using Wcs.Core.TransportScheduling;
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

        services.AddSingleton<IDatabaseInitializer>(sp =>
            new DatabaseInitializer(
                connectionString,
                sp.GetRequiredService<ILogger<DatabaseInitializer>>()));

        services.AddSingleton<IAlarmQueryService, AlarmQueryService>();
        services.AddSingleton<ITaskQueryService, TaskQueryService>();
        services.AddSingleton<IDeviceQueryService, DeviceQueryService>();

        services.Replace(ServiceDescriptor.Singleton<ITransportConfigurationStore>(
            _ => new SqlSugarTransportConfigurationStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<ITransportJournalStore>(
            _ => new SqlSugarTransportJournalStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<ITransportGovernanceStore>(
            _ => new SqlSugarTransportGovernanceStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<ITransportPlcSignalMapStore>(
            _ => new SqlSugarTransportPlcSignalMapStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<ITransportStateStore>(
            _ => new SqlSugarTransportStateStore(connectionString)));
        services.Replace(ServiceDescriptor.Singleton<ITransportCommissioningStore>(
            _ => new SqlSugarTransportCommissioningStore(connectionString)));

        return services;
    }
}
