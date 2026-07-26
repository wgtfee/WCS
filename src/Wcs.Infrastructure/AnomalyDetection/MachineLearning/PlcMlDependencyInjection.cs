namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Infrastructure.AnomalyDetection.MachineLearning;

public static class PlcMlDependencyInjection
{
    public static IServiceCollection AddPlcMachineLearning(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var options = configuration
            .GetSection("AnomalyDetection:MachineLearning")
            .Get<PlcMlAnomalyOptions>() ?? new PlcMlAnomalyOptions();

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
            profile.WarningThreshold = Math.Clamp(
                Math.Max(profile.WarningThreshold, profile.ObserveThreshold),
                0,
                1);
            profile.AlarmThreshold = Math.Clamp(
                Math.Max(profile.AlarmThreshold, profile.WarningThreshold),
                0,
                1);
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

            if (profile.Enabled && profile.Signals.Count == 0)
                throw new InvalidOperationException($"PLC ML Profile {profile.ProfileId} 至少需要一个信号定义。");
        }

        services.AddSingleton(options);
        services.AddSingleton<PlcFeatureWindowEngine>();
        services.AddSingleton<IPlcMlModelStore, FilePlcMlModelStore>();
        services.AddSingleton<IPlcMlTrainingStore, FilePlcMlTrainingStore>();
        services.AddSingleton<IPlcMlGovernanceStore>(_ => new SqlSugarPlcMlGovernanceStore(connectionString));
        services.AddSingleton<PlcMlAnomalyEngine>();
        services.AddSingleton<IPlcMlAnomalyEngine>(sp => sp.GetRequiredService<PlcMlAnomalyEngine>());
        services.AddSingleton<IPlcMlGovernanceService, PlcMlGovernanceService>();
        services.AddHostedService<PlcMlAnomalyBackgroundService>();
        return services;
    }
}
