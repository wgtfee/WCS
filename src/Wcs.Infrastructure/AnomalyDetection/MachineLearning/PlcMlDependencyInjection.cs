namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;
using Wcs.Core.EventBus.Publisher;
using Wcs.Infrastructure.AnomalyDetection.Fusion;
using Wcs.Infrastructure.AnomalyDetection.MachineLearning;
using Wcs.Infrastructure.AnomalyDetection.MachineLearning.Adapters;

public static class PlcMlDependencyInjection
{
    public static IServiceCollection AddPlcMachineLearning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("WcsDb");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("WcsDb connection string is required.");
        return AddPlcMachineLearning(services, configuration, configured);
    }

    public static IServiceCollection AddPlcMachineLearning(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var options = configuration
            .GetSection("AnomalyDetection:MachineLearning")
            .Get<PlcMlAnomalyOptions>() ?? new PlcMlAnomalyOptions();
        var pluggableOptions = configuration
            .GetSection("AnomalyDetection:MachineLearning:PluggableRuntime")
            .Get<PlcMlPluggableRuntimeOptions>() ?? new PlcMlPluggableRuntimeOptions();

        options.ModelDirectory = string.IsNullOrWhiteSpace(options.ModelDirectory)
            ? "data/anomaly-models"
            : options.ModelDirectory.Trim();
        options.TrainingDirectory = string.IsNullOrWhiteSpace(options.TrainingDirectory)
            ? "data/anomaly-training"
            : options.TrainingDirectory.Trim();
        options.MaintenanceIntervalMs = Math.Clamp(options.MaintenanceIntervalMs, 100, 60_000);
        options.MaximumTrackedWindows = Math.Clamp(options.MaximumTrackedWindows, 100, 1_000_000);
        options.InactiveInferenceStateRetentionSeconds = Math.Clamp(
            options.InactiveInferenceStateRetentionSeconds,
            1,
            86_400);
        pluggableOptions.MaximumTrackedWindows = Math.Clamp(
            pluggableOptions.MaximumTrackedWindows,
            1,
            1_000_000);
        pluggableOptions.InactiveStateRetentionSeconds = Math.Clamp(
            pluggableOptions.InactiveStateRetentionSeconds,
            1,
            86_400);

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in options.Profiles)
        {
            profile.ProfileId = profile.ProfileId?.Trim() ?? string.Empty;
            if (profile.Enabled && string.IsNullOrWhiteSpace(profile.ProfileId))
                throw new InvalidOperationException("启用的 PLC ML Profile 必须配置 ProfileId。");
            if (!profileIds.Add(profile.ProfileId))
                throw new InvalidOperationException($"PLC ML ProfileId 重复：{profile.ProfileId}。");

            profile.PlcPattern = string.IsNullOrWhiteSpace(profile.PlcPattern) ? "*" : profile.PlcPattern.Trim();
            profile.DevicePattern = string.IsNullOrWhiteSpace(profile.DevicePattern) ? "*" : profile.DevicePattern.Trim();
            profile.WindowSeconds = Math.Clamp(profile.WindowSeconds, 1, 3_600);
            profile.MinimumSamplesPerSignal = Math.Clamp(profile.MinimumSamplesPerSignal, 1, 1_000_000);
            profile.MinimumTrainingWindows = Math.Clamp(profile.MinimumTrainingWindows, 20, 1_000_000);
            profile.MaximumTrainingWindows = Math.Clamp(
                Math.Max(profile.MaximumTrainingWindows, profile.MinimumTrainingWindows),
                profile.MinimumTrainingWindows,
                5_000_000);
            profile.TreeCount = Math.Clamp(profile.TreeCount, 10, 1_000);
            profile.SampleSize = Math.Clamp(profile.SampleSize, 16, 4_096);
            profile.Contamination = Math.Clamp(profile.Contamination, 0.0001, 0.49);
            profile.ObserveThreshold = Math.Clamp(profile.ObserveThreshold, 0, 1);
            profile.WarningThreshold = Math.Clamp(Math.Max(profile.WarningThreshold, profile.ObserveThreshold), 0, 1);
            profile.AlarmThreshold = Math.Clamp(Math.Max(profile.AlarmThreshold, profile.WarningThreshold), 0, 1);
            profile.ConsecutiveAbnormalCount = Math.Clamp(profile.ConsecutiveAbnormalCount, 1, 1_000);
            profile.ConsecutiveRecoveryCount = Math.Clamp(profile.ConsecutiveRecoveryCount, 1, 10_000);
            profile.CanaryPercentage = Math.Clamp(profile.CanaryPercentage, 0, 100);
            profile.DriftWindowSize = Math.Clamp(profile.DriftWindowSize, 20, 100_000);
            profile.MinimumDriftSamples = Math.Clamp(profile.MinimumDriftSamples, 10, profile.DriftWindowSize);
            profile.DriftWarningRatio = Math.Clamp(profile.DriftWarningRatio, 0.01, 10);
            profile.DriftCriticalRatio = Math.Clamp(
                Math.Max(profile.DriftCriticalRatio, profile.DriftWarningRatio),
                profile.DriftWarningRatio,
                20);
            profile.DriftSnapshotIntervalSeconds = Math.Clamp(profile.DriftSnapshotIntervalSeconds, 1, 86_400);

            profile.MinimumPeerDevices = Math.Clamp(profile.MinimumPeerDevices, 3, 10_000);
            profile.PeerBucketWaitMs = Math.Clamp(profile.PeerBucketWaitMs, 0, 60_000);
            profile.PeerBucketRetentionSeconds = Math.Clamp(profile.PeerBucketRetentionSeconds, 1, 86_400);
            profile.PeerMadMultiplier = Math.Clamp(profile.PeerMadMultiplier, 1, 100);
            profile.MinimumPeerMad = Math.Clamp(profile.MinimumPeerMad, 1e-9, 1_000_000);
            profile.ConsecutivePeerAbnormalCount = Math.Clamp(profile.ConsecutivePeerAbnormalCount, 1, 1_000);
            profile.ConsecutivePeerRecoveryCount = Math.Clamp(profile.ConsecutivePeerRecoveryCount, 1, 10_000);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var signal in profile.Signals)
            {
                signal.Name = signal.Name?.Trim() ?? string.Empty;
                signal.Pattern = signal.Pattern?.Trim() ?? string.Empty;
                if (profile.Enabled && (string.IsNullOrWhiteSpace(signal.Name) || string.IsNullOrWhiteSpace(signal.Pattern)))
                    throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 的信号 Name/Pattern 不能为空。");
                if (!names.Add(signal.Name))
                    throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 的信号名称重复：{signal.Name}。");
            }

            var contextNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var context in profile.ContextSignals)
            {
                context.Name = context.Name?.Trim() ?? string.Empty;
                context.Pattern = context.Pattern?.Trim() ?? string.Empty;
                context.DefaultValue = string.IsNullOrWhiteSpace(context.DefaultValue)
                    ? "UNKNOWN"
                    : context.DefaultValue.Trim();
                context.MaximumAgeSeconds = Math.Clamp(context.MaximumAgeSeconds, 1, 86_400);
                if (profile.Enabled && (string.IsNullOrWhiteSpace(context.Name) || string.IsNullOrWhiteSpace(context.Pattern)))
                    throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 的上下文 Name/Pattern 不能为空。");
                if (!contextNames.Add(context.Name))
                    throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 的上下文名称重复：{context.Name}。");
            }

            if (profile.Enabled && profile.Signals.Count == 0)
                throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 至少需要一个信号定义。");
        }

        var externalProfileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in pluggableOptions.Profiles)
        {
            mapping.ProfileId = mapping.ProfileId?.Trim() ?? string.Empty;
            if (pluggableOptions.Enabled && mapping.ProfileId.Length == 0)
                throw new InvalidOperationException("PluggableRuntime ProfileId 不能为空。");
            if (!externalProfileIds.Add(mapping.ProfileId))
                throw new InvalidOperationException($"PluggableRuntime ProfileId 重复：{mapping.ProfileId}。");
            if (pluggableOptions.Enabled && !profileIds.Contains(mapping.ProfileId))
                throw new InvalidOperationException($"PluggableRuntime Profile 不存在：{mapping.ProfileId}。");
        }
        if (!pluggableOptions.Enabled) externalProfileIds.Clear();

        services.AddSingleton(options);
        services.AddSingleton(pluggableOptions);
        services.AddSingleton<PlcFeatureWindowEngine>();
        services.AddSingleton<IPlcMlModelStore, FilePlcMlModelStore>();
        services.AddSingleton<IPlcMlExternalModelStore, FilePlcMlExternalModelStore>();
        services.AddSingleton<IPlcMlModelAdapter, IsolationForestPlcMlModelAdapter>();
        services.AddSingleton<IPlcMlModelAdapter, OnnxPlcMlModelAdapter>();
        services.AddSingleton<PlcMlModelAdapterRegistry>();
        services.AddSingleton<IPlcMlTrainingStore, FilePlcMlTrainingStore>();
        services.AddSingleton<IPlcMlGovernanceStore>(_ => new SqlSugarPlcMlGovernanceStore(connectionString));
        services.AddSingleton<PlcMlOperatingContextCenter>();
        services.AddSingleton<PlcMlPeerComparisonEngine>();
        services.AddSingleton<IPlcMlContextPeerRuntime, PlcMlContextPeerRuntime>();
        services.AddSingleton(sp =>
        {
            var legacyOptions = CreateLegacyOptions(options, externalProfileIds);
            return new PlcMlAnomalyEngine(
                legacyOptions,
                new PlcFeatureWindowEngine(legacyOptions),
                sp.GetRequiredService<IPlcMlModelStore>(),
                sp.GetRequiredService<IPlcMlTrainingStore>(),
                sp.GetRequiredService<IPlcMlGovernanceStore>(),
                sp.GetRequiredService<IEventBus>());
        });
        services.AddSingleton<PluggablePlcMlAnomalyEngine>();
        services.AddSingleton<IPlcMlAnomalyEngine>(sp =>
            sp.GetRequiredService<PluggablePlcMlAnomalyEngine>());
        services.AddSingleton<IPlcMlExternalRuntimeStatusProvider>(sp =>
            sp.GetRequiredService<PluggablePlcMlAnomalyEngine>());
        services.AddSingleton<IPlcMlGovernanceService, PlcMlGovernanceService>();
        if (options.Enabled || options.ManagementApiEnabled)
            services.AddHostedService(_ => new PlcMlGovernanceSchemaService(connectionString));
        services.AddHostedService<PlcMlAnomalyBackgroundService>();
        services.AddAnomalyEvidenceFusion(configuration, connectionString);
        services.AddAssetHealthMaintenance(configuration, connectionString);
        services.AddAssetFailureForecast(configuration, connectionString);
        return services;
    }

    private static PlcMlAnomalyOptions CreateLegacyOptions(
        PlcMlAnomalyOptions source,
        IReadOnlySet<string> externalProfileIds) => new()
    {
        Enabled = source.Enabled,
        ManagementApiEnabled = source.ManagementApiEnabled,
        ModelDirectory = source.ModelDirectory,
        TrainingDirectory = source.TrainingDirectory,
        MaintenanceIntervalMs = source.MaintenanceIntervalMs,
        MaximumTrackedWindows = source.MaximumTrackedWindows,
        InactiveInferenceStateRetentionSeconds = source.InactiveInferenceStateRetentionSeconds,
        Profiles = source.Profiles
            .Where(profile => !externalProfileIds.Contains(profile.ProfileId))
            .ToList()
    };
}
