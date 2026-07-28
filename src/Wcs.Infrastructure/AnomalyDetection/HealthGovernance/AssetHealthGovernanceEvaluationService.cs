namespace Wcs.Infrastructure.AnomalyDetection.HealthGovernance;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 周期读取 v3.4 只读评分并推进 v3.5 健康事件状态机。
/// </summary>
public sealed class AssetHealthGovernanceEvaluationService : BackgroundService
{
    private readonly AssetHealthGovernanceOptions _options;
    private readonly AssetHealthScoringOptions _healthOptions;
    private readonly IAssetHealthScoringService _scoring;
    private readonly IAssetHealthGovernanceService _governance;
    private readonly IAssetHealthEventJournalStore _journal;
    private readonly ILogger<AssetHealthGovernanceEvaluationService> _logger;

    public AssetHealthGovernanceEvaluationService(
        AssetHealthGovernanceOptions options,
        AssetHealthScoringOptions healthOptions,
        IAssetHealthScoringService scoring,
        IAssetHealthGovernanceService governance,
        IAssetHealthEventJournalStore journal,
        ILogger<AssetHealthGovernanceEvaluationService> logger)
    {
        _options = options;
        _healthOptions = healthOptions;
        _scoring = scoring;
        _governance = governance;
        _journal = journal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Asset health governance is disabled.");
            return;
        }

        await InitializeAsync(stoppingToken);
        if (!_healthOptions.Enabled)
        {
            _logger.LogWarning(
                "Asset health governance is enabled while AnomalyHealthScoring is disabled; no events will be evaluated.");
        }

        _logger.LogInformation(
            "Asset health governance evaluation started. IntervalSeconds={IntervalSeconds}, MinimumGrade={MinimumGrade}, MES={MesEnabled}",
            _options.EvaluationIntervalSeconds,
            _options.MinimumEventGrade,
            _options.MesPushEnabled);

        await EvaluateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await EvaluateAsync(stoppingToken);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _journal.InitializeAsync(cancellationToken);
                var latest = await _journal.LoadLatestAsync(
                    _options.MaximumTrackedAssets,
                    cancellationToken);
                _governance.Restore(latest);
                _logger.LogInformation(
                    "Asset health governance restored. Events={EventCount}, Active={ActiveCount}",
                    latest.Count,
                    latest.Count(static item =>
                        item.Event.LifecycleStatus == AssetHealthEventLifecycleStatus.Active));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Asset health governance initialization failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var snapshots = _scoring.GetAssets(
                minimumGrade: null,
                maximumCount: Math.Min(_options.MaximumTrackedAssets, 10_000));
            foreach (var snapshot in snapshots)
                await _governance.EvaluateAsync(snapshot, utcNow, cancellationToken);

            await _governance.MaintainAsync(utcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Asset health governance evaluation failed.");
        }
    }
}
