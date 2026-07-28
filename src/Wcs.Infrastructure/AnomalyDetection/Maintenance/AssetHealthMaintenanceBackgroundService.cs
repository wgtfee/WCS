namespace Wcs.Infrastructure.AnomalyDetection.Maintenance;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.Maintenance;
using Wcs.Core.AnomalyDetection.RootCause;

/// <summary>
/// 周期读取已人工复核的 v3.6 根因和 v3.5 活动健康事件，生成 v3.7 检查建议。
/// 规则、SQL 或反馈异常只影响辅助诊断，不进入 PLC、任务或调度控制链路。
/// </summary>
public sealed class AssetHealthMaintenanceBackgroundService : BackgroundService,
    IAssetHealthMaintenanceRuntimeStatus
{
    private readonly AssetHealthMaintenanceOptions _options;
    private readonly AssetHealthRootCauseOptions _rootCauseOptions;
    private readonly IAssetHealthGovernanceService _governance;
    private readonly IAssetHealthRootCauseAnalysisStore _rootCauseStore;
    private readonly IAssetHealthMaintenanceDecisionEngine _engine;
    private readonly IAssetHealthMaintenanceStore _store;
    private readonly ILogger<AssetHealthMaintenanceBackgroundService> _logger;
    private readonly object _statusGate = new();
    private DateTime? _lastEvaluationUtc;
    private string? _lastError;

    public AssetHealthMaintenanceBackgroundService(
        AssetHealthMaintenanceOptions options,
        AssetHealthRootCauseOptions rootCauseOptions,
        IAssetHealthGovernanceService governance,
        IAssetHealthRootCauseAnalysisStore rootCauseStore,
        IAssetHealthMaintenanceDecisionEngine engine,
        IAssetHealthMaintenanceStore store,
        ILogger<AssetHealthMaintenanceBackgroundService> logger)
    {
        _options = options;
        _rootCauseOptions = rootCauseOptions;
        _governance = governance;
        _rootCauseStore = rootCauseStore;
        _engine = engine;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Asset health maintenance decision support is disabled.");
            return;
        }

        await InitializeAsync(stoppingToken);
        if (!_rootCauseOptions.Enabled)
        {
            _logger.LogWarning(
                "Asset health maintenance is enabled while AssetHealthRootCause is disabled; no reviewed analyses will be processed.");
        }

        _logger.LogInformation(
            "Asset health maintenance started. RuleSetVersion={RuleSetVersion}, Rules={Rules}, IntervalSeconds={IntervalSeconds}",
            _engine.RuleSetRegistration.Version,
            _engine.RuleSetRegistration.RuleCount,
            _options.EvaluationIntervalSeconds);

        await EvaluateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await EvaluateAsync(stoppingToken);
    }

    public AssetHealthMaintenanceStatus GetStatus()
    {
        DateTime? lastEvaluation;
        string? lastError;
        lock (_statusGate)
        {
            lastEvaluation = _lastEvaluationUtc;
            lastError = _lastError;
        }
        var registration = _engine.RuleSetRegistration;
        return new AssetHealthMaintenanceStatus
        {
            Enabled = _options.Enabled,
            RootCauseEnabled = _rootCauseOptions.Enabled,
            RuleSetValid = true,
            RuleSetVersion = registration.Version,
            RuleSetHash = registration.RuleSetHash,
            RuleCount = registration.RuleCount,
            LastEvaluationUtc = lastEvaluation,
            LastError = lastError
        };
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _store.InitializeAsync(cancellationToken);
                await _store.RegisterRuleSetAsync(_engine.RuleSetRegistration, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                _logger.LogWarning(exception, "Asset health maintenance initialization failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var maximum = _options.MaximumRecommendationsQueryCount;
            var confirmed = await _rootCauseStore.GetAnalysesAsync(
                reviewDecision: RootCauseReviewDecision.Confirmed,
                maximumCount: maximum,
                cancellationToken: cancellationToken);
            var supplemented = await _rootCauseStore.GetAnalysesAsync(
                reviewDecision: RootCauseReviewDecision.Supplemented,
                maximumCount: maximum,
                cancellationToken: cancellationToken);

            foreach (var analysis in confirmed
                         .Concat(supplemented)
                         .GroupBy(item => item.AnalysisId, StringComparer.Ordinal)
                         .Select(group => group.First())
                         .OrderBy(item => item.AnalyzedAtUtc))
            {
                var healthEvent = _governance.GetEvent(analysis.TriggerEventId);
                if (healthEvent is null || healthEvent.LifecycleStatus != AssetHealthEventLifecycleStatus.Active)
                    continue;
                var recommendation = _engine.Generate(analysis, healthEvent, utcNow);
                if (recommendation is null) continue;
                if (await _store.SaveRecommendationAsync(recommendation, cancellationToken))
                {
                    lock (_statusGate) _lastEvaluationUtc = utcNow;
                }
            }

            await _store.MaintainAsync(utcNow, cancellationToken);
            lock (_statusGate) _lastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
            _logger.LogError(exception, "Asset health maintenance evaluation cycle failed.");
        }
    }

    private void SetError(string error)
    {
        lock (_statusGate) _lastError = error;
    }
}
