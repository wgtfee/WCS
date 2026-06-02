namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.Recovery;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 快照后台服务 - 定时保存 StateCenter 快照（默认 5 秒一次）
/// </summary>
public class SnapshotBackgroundService : BackgroundService
{
    private readonly IStateCenter _stateCenter;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly ILogger<SnapshotBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public SnapshotBackgroundService(
        IStateCenter stateCenter,
        ISnapshotRepository snapshotRepo,
        ILogger<SnapshotBackgroundService> logger,
        TimeSpan? interval = null)
    {
        _stateCenter = stateCenter;
        _snapshotRepo = snapshotRepo;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Snapshot service started (interval: {Interval})", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stateCenter.GetSnapshot();
                await _snapshotRepo.SaveSnapshotAsync(snapshot, stoppingToken);
                await _snapshotRepo.CleanupOldSnapshotsAsync(100, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot save failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
