namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AnomalyDetection.Fusion;

public static class AnomalyFusionDependencyInjection
{
    public static IServiceCollection AddAnomalyEvidenceFusion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection("AnomalyFusion")
            .Get<AnomalyFusionOptions>() ?? new AnomalyFusionOptions();

        options.ChannelCapacity = Math.Clamp(options.ChannelCapacity, 100, 1_000_000);
        options.EvidenceRetentionSeconds = Math.Clamp(options.EvidenceRetentionSeconds, 1, 86_400);
        options.RecoveredEvidenceRetentionSeconds = Math.Clamp(
            options.RecoveredEvidenceRetentionSeconds,
            1,
            86_400);
        options.InactiveStateRetentionSeconds = Math.Clamp(
            options.InactiveStateRetentionSeconds,
            1,
            604_800);
        options.MaximumTrackedAssets = Math.Clamp(options.MaximumTrackedAssets, 100, 1_000_000);
        options.MaximumEvidencePerAsset = Math.Clamp(options.MaximumEvidencePerAsset, 1, 10_000);
        options.MaximumSnapshots = Math.Clamp(options.MaximumSnapshots, 100, 1_000_000);
        options.ObserveThreshold = Math.Clamp(options.ObserveThreshold, 0, 1);
        options.WarningThreshold = Math.Clamp(
            Math.Max(options.WarningThreshold, options.ObserveThreshold),
            0,
            1);
        options.AlarmThreshold = Math.Clamp(
            Math.Max(options.AlarmThreshold, options.WarningThreshold),
            0,
            1);
        options.RecoveryThreshold = Math.Clamp(
            Math.Min(options.RecoveryThreshold, options.WarningThreshold),
            0,
            1);
        options.MinimumIndependentSourcesForAlarm = Math.Clamp(
            options.MinimumIndependentSourcesForAlarm,
            1,
            20);
        options.ConsecutiveWarningEvaluations = Math.Clamp(
            options.ConsecutiveWarningEvaluations,
            1,
            1_000);
        options.ConsecutiveAlarmEvaluations = Math.Clamp(
            options.ConsecutiveAlarmEvaluations,
            1,
            1_000);
        options.ConsecutiveRecoveryEvaluations = Math.Clamp(
            options.ConsecutiveRecoveryEvaluations,
            1,
            10_000);
        options.SourceDiversityBonus = Math.Clamp(options.SourceDiversityBonus, 0, 0.5);
        options.MaximumSourceDiversityBonus = Math.Clamp(
            Math.Max(options.MaximumSourceDiversityBonus, options.SourceDiversityBonus),
            options.SourceDiversityBonus,
            0.5);

        foreach (var source in options.Sources)
        {
            source.Source = source.Source?.Trim() ?? string.Empty;
            source.Weight = Math.Clamp(source.Weight, 0, 2);
            source.DefaultConfidence = Math.Clamp(source.DefaultConfidence, 0, 1);
        }

        services.AddSingleton(options);
        services.AddSingleton<AnomalyFusionEngine>();
        services.AddSingleton<IAnomalyFusionEngine>(sp =>
            sp.GetRequiredService<AnomalyFusionEngine>());
        services.AddSingleton<AnomalyEvidenceChannel>();
        services.AddSingleton<IAnomalyEvidenceSink>(sp =>
            sp.GetRequiredService<AnomalyEvidenceChannel>());
        services.AddSingleton<IAnomalyEvidenceIngressStatus>(sp =>
            sp.GetRequiredService<AnomalyEvidenceChannel>());
        services.AddHostedService<AnomalyFusionBackgroundService>();
        services.AddHostedService<PlcAnomalyFusionBridgeService>();
        services.AddHostedService<TransportCycleFusionBridgeService>();
        return services;
    }
}
