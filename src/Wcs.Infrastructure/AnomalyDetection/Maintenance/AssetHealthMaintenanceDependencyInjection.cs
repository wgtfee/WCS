namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wcs.Core.AnomalyDetection.Maintenance;
using Wcs.Infrastructure.AnomalyDetection.Maintenance;

public static class AssetHealthMaintenanceDependencyInjection
{
    public static IServiceCollection AddAssetHealthMaintenance(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var options = configuration
            .GetSection("AssetHealthMaintenance")
            .Get<AssetHealthMaintenanceOptions>() ?? new AssetHealthMaintenanceOptions();

        options.EvaluationIntervalSeconds = Math.Clamp(options.EvaluationIntervalSeconds, 1, 3_600);
        options.MaximumRules = Math.Clamp(options.MaximumRules, 1, 100_000);
        options.MaximumItemsPerRecommendation = Math.Clamp(
            options.MaximumItemsPerRecommendation,
            1,
            1_000);
        options.MaximumRecommendationsQueryCount = Math.Clamp(
            options.MaximumRecommendationsQueryCount,
            1,
            10_000);
        options.MinimumRootCauseConfidence = Math.Clamp(
            options.MinimumRootCauseConfidence,
            0,
            1);
        options.RecommendationRetentionHours = Math.Clamp(
            options.RecommendationRetentionHours,
            1,
            87_600);
        options.MaintenanceIntervalSeconds = Math.Clamp(
            options.MaintenanceIntervalSeconds,
            1,
            86_400);
        options.MaintenanceBatchSize = Math.Clamp(
            options.MaintenanceBatchSize,
            100,
            100_000);
        options.RuleSet ??= new MaintenanceRuleSetDefinition();
        options.RuleSet.Rules ??= new List<MaintenanceDecisionRule>();

        services.AddSingleton(options);
        services.AddSingleton<IAssetHealthMaintenanceDecisionEngine, AssetHealthMaintenanceDecisionEngine>();
        services.AddSingleton<SqlSugarAssetHealthMaintenanceStore>(sp => new(
            connectionString,
            options,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlSugarAssetHealthMaintenanceStore>>()));
        services.AddSingleton<IAssetHealthMaintenanceStore>(sp =>
            sp.GetRequiredService<SqlSugarAssetHealthMaintenanceStore>());
        services.AddSingleton<AssetHealthMaintenanceBackgroundService>();
        services.AddSingleton<IAssetHealthMaintenanceRuntimeStatus>(sp =>
            sp.GetRequiredService<AssetHealthMaintenanceBackgroundService>());
        if (options.Enabled)
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<AssetHealthMaintenanceBackgroundService>());
        return services;
    }
}
