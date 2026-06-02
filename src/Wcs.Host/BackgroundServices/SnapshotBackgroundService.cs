namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wcs.Core.Common.Options;
using Wcs.Core.Recovery;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 快照后台服务 - 定时保存 StateCenter 快照
/// </summary>
public class SnapshotBackgroundService : BackgroundService
{
    private readonly IStateCenter _stateCenter;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly ILogger<SnapshotBackgroundService> _logger;
    private readonly IOptionsMonitor<WcsOptions> _options;

    public SnapshotBackgroundService(
        IStateCenter stateCenter,
        ISnapshotRepository snapshotRepo,
        ILogger<SnapshotBackgroundService> logger,
        IOptionsMonitor<WcsOptions> options)
    {
        _stateCenter = stateCenter;
        _snapshotRepo = snapshotRepo;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue.Snapshot;
        _logger.LogInformation("Snapshot service started (interval: {Interval}s)", opts.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stateCenter.GetSnapshot();
                await _snapshotRepo.SaveSnapshotAsync(snapshot, stoppingToken);
                await _snapshotRepo.CleanupOldSnapshotsAsync(
                    _options.CurrentValue.Snapshot.MaxSnapshots, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot save failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.Snapshot.IntervalSeconds), stoppingToken);
        }
    }
}
