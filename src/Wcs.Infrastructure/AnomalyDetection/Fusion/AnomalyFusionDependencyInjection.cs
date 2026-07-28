namespace Wcs.Infrastructure.AnomalyDetection.Fusion;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.RootCause;
using Wcs.Infrastructure.AnomalyDetection.HealthGovernance;
using Wcs.Infrastructure.AnomalyDetection.HealthScoring;
using Wcs.Infrastructure.AnomalyDetection.RootCause;

public static class AnomalyFusionDependencyInjection
{
    public static IServiceCollection AddAnomalyEvidenceFusion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("WcsDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("WcsDb connection string is required.");
        return AddAnomalyEvidenceFusion(services, configuration, connectionString);
    }

    public static IServiceCollection AddAnomalyEvidenceFusion(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
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

        var healthOptions = configuration
            .GetSection("AnomalyHealthScoring")
            .Get<AssetHealthScoringOptions>() ?? new AssetHealthScoringOptions();
        healthOptions.HealthyMinimumScore = Math.Clamp(healthOptions.HealthyMinimumScore, 1, 100);
        healthOptions.AttentionMinimumScore = Math.Clamp(
            Math.Min(healthOptions.AttentionMinimumScore, healthOptions.HealthyMinimumScore),
            0,
            100);
        healthOptions.DegradedMinimumScore = Math.Clamp(
            Math.Min(healthOptions.DegradedMinimumScore, healthOptions.AttentionMinimumScore),
            0,
            100);
        healthOptions.MaximumFactors = Math.Clamp(healthOptions.MaximumFactors, 1, 100);
        healthOptions.SamplingIntervalSeconds = Math.Clamp(healthOptions.SamplingIntervalSeconds, 1, 3_600);
        healthOptions.MinimumScoreChangeToRecord = Math.Clamp(healthOptions.MinimumScoreChangeToRecord, 0, 100);
        healthOptions.MaximumUnchangedIntervalSeconds = Math.Clamp(
            Math.Max(healthOptions.MaximumUnchangedIntervalSeconds, healthOptions.SamplingIntervalSeconds),
            healthOptions.SamplingIntervalSeconds,
            86_400);
        healthOptions.MaximumHistoryPerAsset = Math.Clamp(healthOptions.MaximumHistoryPerAsset, 2, 100_000);
        healthOptions.MaximumTrackedHistoryAssets = Math.Clamp(
            healthOptions.MaximumTrackedHistoryAssets,
            100,
            100_000);
        healthOptions.HistoryRetentionHours = Math.Clamp(healthOptions.HistoryRetentionHours, 1, 87_600);
        healthOptions.TrendWindowSize = Math.Clamp(
            healthOptions.TrendWindowSize,
            2,
            healthOptions.MaximumHistoryPerAsset);
        healthOptions.TrendChangeThreshold = Math.Clamp(healthOptions.TrendChangeThreshold, 0, 100);
        healthOptions.MaximumHistoryQueryCount = Math.Clamp(
            healthOptions.MaximumHistoryQueryCount,
            1,
            Math.Min(10_000, healthOptions.MaximumHistoryPerAsset));
        healthOptions.HistoryWriteChannelCapacity = Math.Clamp(
            healthOptions.HistoryWriteChannelCapacity,
            100,
            1_000_000);
        healthOptions.HistoryWriteBatchSize = Math.Clamp(
            healthOptions.HistoryWriteBatchSize,
            1,
            Math.Min(10_000, healthOptions.HistoryWriteChannelCapacity));
        healthOptions.HistoryWriteRetryDelayMs = Math.Clamp(
            healthOptions.HistoryWriteRetryDelayMs,
            100,
            60_000);
        healthOptions.HistoryMaintenanceIntervalSeconds = Math.Clamp(
            healthOptions.HistoryMaintenanceIntervalSeconds,
            1,
            86_400);
        healthOptions.HistoryMaintenanceBatchSize = Math.Clamp(
            healthOptions.HistoryMaintenanceBatchSize,
            100,
            100_000);

        var governanceOptions = configuration
            .GetSection("AssetHealthGovernance")
            .Get<AssetHealthGovernanceOptions>() ?? new AssetHealthGovernanceOptions();
        if (!Enum.IsDefined(governanceOptions.MinimumEventGrade))
            governanceOptions.MinimumEventGrade = AssetHealthGrade.Degraded;
        governanceOptions.EvaluationIntervalSeconds = Math.Clamp(
            governanceOptions.EvaluationIntervalSeconds,
            1,
            3_600);
        governanceOptions.ConsecutiveUnhealthyEvaluations = Math.Clamp(
            governanceOptions.ConsecutiveUnhealthyEvaluations,
            1,
            10_000);
        governanceOptions.ConsecutiveRecoveryEvaluations = Math.Clamp(
            governanceOptions.ConsecutiveRecoveryEvaluations,
            1,
            10_000);
        governanceOptions.MaximumUnchangedEventIntervalSeconds = Math.Clamp(
            Math.Max(
                governanceOptions.MaximumUnchangedEventIntervalSeconds,
                governanceOptions.EvaluationIntervalSeconds),
            governanceOptions.EvaluationIntervalSeconds,
            86_400);
        governanceOptions.MaximumTrackedAssets = Math.Clamp(
            governanceOptions.MaximumTrackedAssets,
            100,
            100_000);
        governanceOptions.MaximumEventsQueryCount = Math.Clamp(
            governanceOptions.MaximumEventsQueryCount,
            1,
            10_000);
        governanceOptions.InactiveStateRetentionSeconds = Math.Clamp(
            governanceOptions.InactiveStateRetentionSeconds,
            governanceOptions.EvaluationIntervalSeconds,
            604_800);
        governanceOptions.EventRetentionHours = Math.Clamp(
            governanceOptions.EventRetentionHours,
            1,
            87_600);
        governanceOptions.MaintenanceIntervalSeconds = Math.Clamp(
            governanceOptions.MaintenanceIntervalSeconds,
            1,
            86_400);
        governanceOptions.MaintenanceBatchSize = Math.Clamp(
            governanceOptions.MaintenanceBatchSize,
            100,
            100_000);
        governanceOptions.MesEndpointPath = string.IsNullOrWhiteSpace(governanceOptions.MesEndpointPath)
            ? "/api/wcs/asset-health-events"
            : governanceOptions.MesEndpointPath.Trim();
        governanceOptions.MesTimeoutSeconds = Math.Clamp(governanceOptions.MesTimeoutSeconds, 1, 120);
        governanceOptions.MesPollIntervalSeconds = Math.Clamp(
            governanceOptions.MesPollIntervalSeconds,
            1,
            300);
        governanceOptions.MesBatchSize = Math.Clamp(governanceOptions.MesBatchSize, 1, 10_000);
        governanceOptions.MesMaximumAttempts = Math.Clamp(
            governanceOptions.MesMaximumAttempts,
            1,
            10_000);
        governanceOptions.MesInitialRetrySeconds = Math.Clamp(
            governanceOptions.MesInitialRetrySeconds,
            1,
            3_600);
        governanceOptions.MesMaximumRetrySeconds = Math.Clamp(
            Math.Max(governanceOptions.MesMaximumRetrySeconds, governanceOptions.MesInitialRetrySeconds),
            governanceOptions.MesInitialRetrySeconds,
            86_400);
        governanceOptions.MesApiKeyHeader = governanceOptions.MesApiKeyHeader?.Trim() ?? string.Empty;
        if (governanceOptions.Enabled && governanceOptions.MesPushEnabled)
        {
            if (!Uri.TryCreate(governanceOptions.MesBaseUrl, UriKind.Absolute, out var mesUri) ||
                mesUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException(
                    "AssetHealthGovernance:MesBaseUrl must be an absolute HTTP/HTTPS URL when MES push is enabled.");
        }

        var rootCauseOptions = configuration
            .GetSection("AssetHealthRootCause")
            .Get<AssetHealthRootCauseOptions>() ?? new AssetHealthRootCauseOptions();
        rootCauseOptions.EvaluationIntervalSeconds = Math.Clamp(
            rootCauseOptions.EvaluationIntervalSeconds,
            1,
            3_600);
        rootCauseOptions.CorrelationWindowSeconds = Math.Clamp(
            rootCauseOptions.CorrelationWindowSeconds,
            1,
            86_400);
        rootCauseOptions.MaximumPropagationDepth = Math.Clamp(
            rootCauseOptions.MaximumPropagationDepth,
            1,
            100);
        rootCauseOptions.MaximumGraphNodes = Math.Clamp(
            rootCauseOptions.MaximumGraphNodes,
            1,
            1_000_000);
        rootCauseOptions.MaximumGraphEdges = Math.Clamp(
            rootCauseOptions.MaximumGraphEdges,
            0,
            2_000_000);
        rootCauseOptions.MaximumEventsPerAnalysis = Math.Clamp(
            rootCauseOptions.MaximumEventsPerAnalysis,
            1,
            10_000);
        rootCauseOptions.MaximumCandidates = Math.Clamp(
            rootCauseOptions.MaximumCandidates,
            1,
            100);
        rootCauseOptions.MaximumPaths = Math.Clamp(
            rootCauseOptions.MaximumPaths,
            1,
            10_000);
        rootCauseOptions.MaximumAnalysesQueryCount = Math.Clamp(
            rootCauseOptions.MaximumAnalysesQueryCount,
            1,
            10_000);
        rootCauseOptions.MinimumCandidateConfidence = Math.Clamp(
            rootCauseOptions.MinimumCandidateConfidence,
            0,
            1);
        rootCauseOptions.AnalysisRetentionHours = Math.Clamp(
            rootCauseOptions.AnalysisRetentionHours,
            1,
            87_600);
        rootCauseOptions.MaintenanceIntervalSeconds = Math.Clamp(
            rootCauseOptions.MaintenanceIntervalSeconds,
            1,
            86_400);
        rootCauseOptions.MaintenanceBatchSize = Math.Clamp(
            rootCauseOptions.MaintenanceBatchSize,
            100,
            100_000);
        rootCauseOptions.Graph ??= new RootCauseGraphDefinition();
        rootCauseOptions.Graph.Nodes ??= new List<RootCauseGraphNode>();
        rootCauseOptions.Graph.Edges ??= new List<RootCauseGraphEdge>();

        services.AddSingleton(options);
        services.AddSingleton(healthOptions);
        services.AddSingleton(governanceOptions);
        services.AddSingleton(rootCauseOptions);
        services.AddSingleton<AnomalyFusionEngine>();
        services.AddSingleton<IAnomalyFusionEngine>(sp =>
            sp.GetRequiredService<AnomalyFusionEngine>());
        services.AddSingleton<IAssetHealthScoringService, AssetHealthScoringService>();

        if (healthOptions.HistoryProvider == AssetHealthHistoryProvider.SqlServer)
        {
            services.AddSingleton<SqlSugarAssetHealthScoreHistoryStore>(sp => new(
                connectionString,
                healthOptions,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlSugarAssetHealthScoreHistoryStore>>()));
            services.AddSingleton<IAssetHealthScoreHistoryStore>(sp =>
                sp.GetRequiredService<SqlSugarAssetHealthScoreHistoryStore>());
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<SqlSugarAssetHealthScoreHistoryStore>());
        }
        else
        {
            services.AddSingleton<IAssetHealthScoreHistoryStore, InMemoryAssetHealthScoreHistoryStore>();
        }

        services.AddSingleton<SqlSugarAssetHealthEventJournalStore>(sp => new(
            connectionString,
            governanceOptions,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlSugarAssetHealthEventJournalStore>>()));
        services.AddSingleton<IAssetHealthEventJournalStore>(sp =>
            sp.GetRequiredService<SqlSugarAssetHealthEventJournalStore>());
        services.AddSingleton<IAssetHealthGovernanceService, AssetHealthGovernanceService>();
        services.AddHttpClient(AssetHealthMesDeliveryService.HttpClientName);

        services.AddSingleton<IAssetHealthRootCauseAnalysisEngine, AssetHealthRootCauseAnalysisEngine>();
        services.AddSingleton<SqlSugarAssetHealthRootCauseAnalysisStore>(sp => new(
            connectionString,
            rootCauseOptions,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlSugarAssetHealthRootCauseAnalysisStore>>()));
        services.AddSingleton<IAssetHealthRootCauseAnalysisStore>(sp =>
            sp.GetRequiredService<SqlSugarAssetHealthRootCauseAnalysisStore>());
        services.AddSingleton<AssetHealthRootCauseAnalysisBackgroundService>();
        services.AddSingleton<IAssetHealthRootCauseRuntimeStatus>(sp =>
            sp.GetRequiredService<AssetHealthRootCauseAnalysisBackgroundService>());

        services.AddSingleton<AnomalyEvidenceChannel>();
        services.AddSingleton<IAnomalyEvidenceSink>(sp =>
            sp.GetRequiredService<AnomalyEvidenceChannel>());
        services.AddSingleton<IAnomalyEvidenceIngressStatus>(sp =>
            sp.GetRequiredService<AnomalyEvidenceChannel>());
        services.AddHostedService<AnomalyFusionBackgroundService>();
        services.AddHostedService<PlcAnomalyFusionBridgeService>();
        services.AddHostedService<TransportCycleFusionBridgeService>();
        if (healthOptions.Enabled)
            services.AddHostedService<AssetHealthScoreSamplingService>();
        if (governanceOptions.Enabled)
            services.AddHostedService<AssetHealthGovernanceEvaluationService>();
        if (governanceOptions.Enabled && governanceOptions.MesPushEnabled)
            services.AddHostedService<AssetHealthMesDeliveryService>();
        if (rootCauseOptions.Enabled)
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<AssetHealthRootCauseAnalysisBackgroundService>());
        return services;
    }
}
