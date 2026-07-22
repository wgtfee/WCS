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
        var backupDirectory = configuration["TransportResilience:BackupDirectory"]
            ?? "data/transport-backups";
        var resilienceOptions = new TransportResilienceOptions
        {
            Enabled = GetBool(configuration, "TransportResilience:Enabled", true),
            PreflightIntervalSeconds = GetInt(configuration, "TransportResilience:PreflightIntervalSeconds", 60),
            AutomaticBackupEnabled = GetBool(configuration, "TransportResilience:AutomaticBackupEnabled", true),
            BackupIntervalMinutes = GetInt(configuration, "TransportResilience:BackupIntervalMinutes", 60),
            BackupRetentionCount = GetInt(configuration, "TransportResilience:BackupRetentionCount", 48),
            MaximumJournalRecords = GetInt(configuration, "TransportResilience:MaximumJournalRecords", 5000),
            MaximumBackupAgeMinutes = GetInt(configuration, "TransportResilience:MaximumBackupAgeMinutes", 180),
            RequireReadyBeforeAutomaticBackup = GetBool(configuration, "TransportResilience:RequireReadyBeforeAutomaticBackup", false),
            BackupDirectory = backupDirectory
        };
        var simulationOptions = new TransportSimulationOptions
        {
            MaximumScenarioTasks = GetInt(configuration, "TransportSimulation:MaximumScenarioTasks", 10000),
            MaximumStoredRuns = GetInt(configuration, "TransportSimulation:MaximumStoredRuns", 200),
            MaximumStoredComparisons = GetInt(configuration, "TransportSimulation:MaximumStoredComparisons", 100),
            ForecastBucketSeconds = GetInt(configuration, "TransportSimulation:ForecastBucketSeconds", 60),
            DefaultTravelSeconds = GetInt(configuration, "TransportSimulation:DefaultTravelSeconds", 30),
            DefaultServiceSeconds = GetInt(configuration, "TransportSimulation:DefaultServiceSeconds", 10),
            SustainableP95WaitingSeconds = GetDouble(configuration, "TransportSimulation:SustainableP95WaitingSeconds", 120),
            SustainableDeadlineMissRatePercent = GetDouble(configuration, "TransportSimulation:SustainableDeadlineMissRatePercent", 5),
            HistoricalJournalLimit = GetInt(configuration, "TransportSimulation:HistoricalJournalLimit", 20000)
        };

        services.Replace(ServiceDescriptor.Singleton<TransportResilienceOptions>(resilienceOptions));
        services.Replace(ServiceDescriptor.Singleton<TransportSimulationOptions>(simulationOptions));
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
        services.Replace(ServiceDescriptor.Singleton<ITransportLogicalBackupStorage>(
            _ => new FileTransportLogicalBackupStorage(backupDirectory)));

        return services;
    }

    private static bool GetBool(IConfiguration configuration, string key, bool defaultValue) =>
        bool.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static int GetInt(IConfiguration configuration, string key, int defaultValue) =>
        int.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static double GetDouble(IConfiguration configuration, string key, double defaultValue) =>
        double.TryParse(configuration[key], out var value) ? value : defaultValue;
}
