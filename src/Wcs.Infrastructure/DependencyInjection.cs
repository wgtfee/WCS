namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.Persistence;
using Wcs.Core.Telemetry;
using Wcs.Core.TransportScheduling;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.Persistence.Services;
using Wcs.Infrastructure.Telemetry;

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
            MaximumScenarioTasks = Math.Clamp(
                GetInt(configuration, "TransportSimulation:MaximumScenarioTasks", 5000),
                1,
                5000),
            MaximumStoredRuns = Math.Clamp(
                GetInt(configuration, "TransportSimulation:MaximumStoredRuns", 200),
                10,
                5000),
            MaximumStoredComparisons = Math.Clamp(
                GetInt(configuration, "TransportSimulation:MaximumStoredComparisons", 100),
                10,
                1000),
            ForecastBucketSeconds = Math.Clamp(
                GetInt(configuration, "TransportSimulation:ForecastBucketSeconds", 60),
                10,
                3600),
            DefaultTravelSeconds = Math.Clamp(
                GetInt(configuration, "TransportSimulation:DefaultTravelSeconds", 30),
                1,
                3600),
            DefaultServiceSeconds = Math.Clamp(
                GetInt(configuration, "TransportSimulation:DefaultServiceSeconds", 10),
                0,
                3600),
            SustainableP95WaitingSeconds = Math.Clamp(
                GetDouble(configuration, "TransportSimulation:SustainableP95WaitingSeconds", 120),
                0,
                86400),
            SustainableDeadlineMissRatePercent = Math.Clamp(
                GetDouble(configuration, "TransportSimulation:SustainableDeadlineMissRatePercent", 5),
                0,
                100),
            HistoricalJournalLimit = Math.Clamp(
                GetInt(configuration, "TransportSimulation:HistoricalJournalLimit", 20000),
                100,
                50000)
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

        AddPlcTelemetryStorage(services, configuration, connectionString);
        AddPlcAnomalyDetection(services, configuration);
        return services;
    }

    private static void AddPlcTelemetryStorage(
        IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var options = configuration
            .GetSection("Storage:Telemetry")
            .Get<PlcTelemetryOptions>() ?? new PlcTelemetryOptions();

        options.ChannelCapacity = Math.Clamp(options.ChannelCapacity, 1_000, 1_000_000);
        options.BatchSize = Math.Clamp(options.BatchSize, 1, Math.Min(10_000, options.ChannelCapacity));
        options.FlushIntervalMs = Math.Clamp(options.FlushIntervalMs, 10, 60_000);
        options.RetryDelayMs = Math.Clamp(options.RetryDelayMs, 100, 60_000);
        options.WalBatchSize = Math.Clamp(options.WalBatchSize, 1, Math.Min(10_000, options.ChannelCapacity));
        options.WalFlushIntervalMs = Math.Clamp(options.WalFlushIntervalMs, 1, 5_000);
        options.Site = string.IsNullOrWhiteSpace(options.Site) ? "default" : options.Site.Trim();
        options.Measurement = string.IsNullOrWhiteSpace(options.Measurement)
            ? "plc_signal"
            : options.Measurement.Trim();
        options.SpoolDirectory = string.IsNullOrWhiteSpace(options.SpoolDirectory)
            ? "data/plc-telemetry-spool"
            : options.SpoolDirectory;

        if (options.Provider == PlcTelemetryProvider.InfluxDb)
        {
            if (!Uri.TryCreate(options.InfluxDb.Url, UriKind.Absolute, out _))
                throw new InvalidOperationException("Storage:Telemetry:InfluxDb:Url 不是有效的绝对地址。");
            if (options.InfluxDb.ApiVersion == InfluxDbApiVersion.V2 &&
                (string.IsNullOrWhiteSpace(options.InfluxDb.Organization) ||
                 string.IsNullOrWhiteSpace(options.InfluxDb.Bucket)))
                throw new InvalidOperationException("InfluxDB V2 必须配置 Organization 和 Bucket。");
            if (options.InfluxDb.ApiVersion == InfluxDbApiVersion.V3 &&
                string.IsNullOrWhiteSpace(options.InfluxDb.Database))
                throw new InvalidOperationException("InfluxDB V3 必须配置 Database。");
        }

        services.Replace(ServiceDescriptor.Singleton(options));
        services.AddSingleton<FilePlcTelemetrySpool>();
        services.AddSingleton<PlcTelemetryBuffer>();
        services.AddSingleton<IPlcTelemetrySink>(sp => sp.GetRequiredService<PlcTelemetryBuffer>());
        services.AddSingleton<IPlcTelemetryStatusProvider>(sp => sp.GetRequiredService<PlcTelemetryBuffer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PlcTelemetryBuffer>());
        services.AddHttpClient("WcsInfluxTelemetry", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<IPlcTelemetryStore>(sp => options.Provider switch
        {
            PlcTelemetryProvider.Disabled => new DisabledPlcTelemetryStore(),
            PlcTelemetryProvider.SqlServer => new SqlServerPlcTelemetryStore(connectionString),
            PlcTelemetryProvider.InfluxDb => new InfluxDbPlcTelemetryStore(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("WcsInfluxTelemetry"),
                options),
            _ => throw new InvalidOperationException($"不支持的 PLC telemetry provider: {options.Provider}")
        });
        services.AddHostedService<PlcTelemetryBatchWriterService>();
    }

    private static void AddPlcAnomalyDetection(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection("AnomalyDetection")
            .Get<PlcAnomalyOptions>() ?? new PlcAnomalyOptions();

        options.WindowSize = Math.Clamp(options.WindowSize, 10, 10_000);
        options.MinimumSamples = Math.Clamp(options.MinimumSamples, 3, options.WindowSize);
        options.MaximumTrackedRuleSignals = Math.Clamp(
            options.MaximumTrackedRuleSignals,
            100,
            1_000_000);
        options.ObserveThreshold = Math.Clamp(options.ObserveThreshold, 0, 1);
        options.WarningThreshold = Math.Clamp(
            Math.Max(options.WarningThreshold, options.ObserveThreshold),
            0,
            1);
        options.AlarmThreshold = Math.Clamp(
            Math.Max(options.AlarmThreshold, options.WarningThreshold),
            0,
            1);
        options.ConsecutiveWarningCount = Math.Clamp(options.ConsecutiveWarningCount, 1, 1_000);
        options.ConsecutiveAlarmCount = Math.Clamp(
            Math.Max(options.ConsecutiveAlarmCount, options.ConsecutiveWarningCount),
            1,
            1_000);
        options.RecoveryCount = Math.Clamp(options.RecoveryCount, 1, 10_000);
        options.DurationSweepIntervalMs = Math.Clamp(options.DurationSweepIntervalMs, 100, 60_000);
        options.AlarmDelayRaiseMs = Math.Clamp(options.AlarmDelayRaiseMs, 0, 60_000);
        options.AlarmDelayRecoverMs = Math.Clamp(options.AlarmDelayRecoverMs, 0, 60_000);

        for (var index = 0; index < options.Rules.Count; index++)
        {
            var rule = options.Rules[index];
            rule.RuleId = string.IsNullOrWhiteSpace(rule.RuleId)
                ? $"ANOMALY-RULE-{index + 1}"
                : rule.RuleId.Trim();
            rule.PlcPattern = string.IsNullOrWhiteSpace(rule.PlcPattern) ? "*" : rule.PlcPattern.Trim();
            rule.DevicePattern = string.IsNullOrWhiteSpace(rule.DevicePattern) ? "*" : rule.DevicePattern.Trim();
            rule.SignalPattern = rule.SignalPattern?.Trim() ?? string.Empty;
            rule.RelatedSignalPattern = string.IsNullOrWhiteSpace(rule.RelatedSignalPattern)
                ? null
                : rule.RelatedSignalPattern.Trim();
            rule.WhenValueEquals = string.IsNullOrWhiteSpace(rule.WhenValueEquals)
                ? null
                : rule.WhenValueEquals.Trim();
            rule.RelatedExpectedValue = string.IsNullOrWhiteSpace(rule.RelatedExpectedValue)
                ? null
                : rule.RelatedExpectedValue.Trim();
            rule.MaximumRelatedAgeMs = Math.Clamp(rule.MaximumRelatedAgeMs, 100, 3_600_000);
            rule.MadMultiplier = Math.Clamp(rule.MadMultiplier, 1, 100);
            rule.MinimumMad = Math.Clamp(rule.MinimumMad, 0.000001, 1_000_000);
            if (rule.MaximumTrueDurationMs is not null)
                rule.MaximumTrueDurationMs = Math.Clamp(rule.MaximumTrueDurationMs.Value, 1, 86_400_000);
            if (rule.ConsecutiveAbnormalCount is not null)
                rule.ConsecutiveAbnormalCount = Math.Clamp(rule.ConsecutiveAbnormalCount.Value, 1, 1_000);
            if (rule.ConsecutiveRecoveryCount is not null)
                rule.ConsecutiveRecoveryCount = Math.Clamp(rule.ConsecutiveRecoveryCount.Value, 1, 10_000);

            if (rule.Enabled && string.IsNullOrWhiteSpace(rule.SignalPattern))
                throw new InvalidOperationException($"AnomalyDetection 规则 {rule.RuleId} 未配置 SignalPattern。");
        }

        services.Replace(ServiceDescriptor.Singleton(options));
        services.AddSingleton<PlcAnomalyEngine>();
        services.AddSingleton<IPlcAnomalyEngine>(sp => sp.GetRequiredService<PlcAnomalyEngine>());
        services.AddSingleton<IPlcAnomalyStatusProvider>(sp => sp.GetRequiredService<PlcAnomalyEngine>());
    }

    private static bool GetBool(IConfiguration configuration, string key, bool defaultValue) =>
        bool.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static int GetInt(IConfiguration configuration, string key, int defaultValue) =>
        int.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static double GetDouble(IConfiguration configuration, string key, double defaultValue) =>
        double.TryParse(configuration[key], out var value) ? value : defaultValue;
}
