namespace Wcs.Infrastructure.AnomalyDetection.HealthScoring;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 周期读取只读健康评分并写入历史仓储。仅记录显著变化、等级变化或周期心跳点。
/// </summary>
public sealed class AssetHealthScoreSamplingService : BackgroundService
{
    private readonly AssetHealthScoringOptions _options;
    private readonly IAssetHealthScoringService _scoring;
    private readonly IAssetHealthScoreHistoryStore _history;
    private readonly ILogger<AssetHealthScoreSamplingService> _logger;

    public AssetHealthScoreSamplingService(
        AssetHealthScoringOptions options,
        IAssetHealthScoringService scoring,
        IAssetHealthScoreHistoryStore history,
        ILogger<AssetHealthScoreSamplingService> logger)
    {
        _options = options;
        _scoring = scoring;
        _history = history;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Asset health score sampling is disabled.");
            return;
        }

        _logger.LogInformation(
            "Asset health score sampling started. IntervalSeconds={IntervalSeconds}, Provider={Provider}",
            _options.SamplingIntervalSeconds,
            _history.Provider);

        await SampleAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SamplingIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SampleAsync(stoppingToken);
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var maximumAssets = Math.Min(_options.MaximumTrackedHistoryAssets, 10_000);
            var snapshots = _scoring.GetAssets(minimumGrade: null, maximumAssets);
            foreach (var snapshot in snapshots)
                await _history.RecordAsync(snapshot, utcNow, cancellationToken);

            await _history.MaintainAsync(utcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Asset health score sampling failed.");
        }
    }
}
