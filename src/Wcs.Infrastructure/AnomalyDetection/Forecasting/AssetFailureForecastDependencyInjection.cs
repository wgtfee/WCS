namespace Wcs.Infrastructure;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Infrastructure.AnomalyDetection.Forecasting;

public static class AssetFailureForecastDependencyInjection
{
    public static IServiceCollection AddAssetFailureForecast(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var options = configuration
            .GetSection("AssetFailureForecast")
            .Get<AssetFailureForecastOptions>() ?? new AssetFailureForecastOptions();
        options.ModelDirectory = string.IsNullOrWhiteSpace(options.ModelDirectory)
            ? "data/failure-forecast-models"
            : options.ModelDirectory.Trim();
        options.EvaluationIntervalSeconds = Math.Clamp(options.EvaluationIntervalSeconds, 10, 86_400);
        options.MinimumHistoryPoints = Math.Clamp(options.MinimumHistoryPoints, 2, 100_000);
        options.MinimumHistorySpanHours = Math.Clamp(options.MinimumHistorySpanHours, 1, 87_600);
        options.MaximumHistoryPoints = Math.Clamp(
            Math.Max(options.MaximumHistoryPoints, options.MinimumHistoryPoints),
            options.MinimumHistoryPoints,
            100_000);
        options.MaximumAssetsPerEvaluation = Math.Clamp(options.MaximumAssetsPerEvaluation, 1, 100_000);
        options.MaximumForecastsQueryCount = Math.Clamp(options.MaximumForecastsQueryCount, 1, 10_000);
        options.ForecastRetentionHours = Math.Clamp(options.ForecastRetentionHours, 1, 175_200);
        options.MaintenanceIntervalSeconds = Math.Clamp(options.MaintenanceIntervalSeconds, 60, 86_400);
        options.MaintenanceBatchSize = Math.Clamp(options.MaintenanceBatchSize, 1, 100_000);
        options.MaximumModelArtifactMegabytes = Math.Clamp(options.MaximumModelArtifactMegabytes, 1, 2_048);
        options.MinimumTrainingAssets = Math.Clamp(options.MinimumTrainingAssets, 2, 1_000_000);
        options.MinimumFailureEvents = Math.Clamp(options.MinimumFailureEvents, 1, 1_000_000);
        options.MinimumValidationAuc = Math.Clamp(options.MinimumValidationAuc, 0.5, 1);
        options.MaximumValidationBrierScore = Math.Clamp(options.MaximumValidationBrierScore, 0, 1);
        options.MinimumPredictionIntervalCoverage = Math.Clamp(options.MinimumPredictionIntervalCoverage, 0, 1);

        services.AddSingleton(options);
        services.AddSingleton<IAssetFailureForecastModelStore, FileAssetFailureForecastModelStore>();
        services.AddSingleton<IAssetFailureForecastStore>(_ =>
            new SqlSugarAssetFailureForecastStore(connectionString, options));
        services.AddSingleton<AssetFailureForecastService>();
        services.AddSingleton<IAssetFailureForecastService>(sp =>
            sp.GetRequiredService<AssetFailureForecastService>());
        services.AddHostedService<AssetFailureForecastBackgroundService>();
        return services;
    }
}
