namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wcs.Core.Common.Options;
using Wcs.Core.Recovery;

/// <summary>
/// 快照后台服务 - 定时保存系统快照（通过 RecoveryManager 协调多模块）
/// </summary>
public class SnapshotBackgroundService : BackgroundService
{
    private readonly IRecoveryManager _recoveryManager;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly ILogger<SnapshotBackgroundService> _logger;
    private readonly IOptionsMonitor<WcsOptions> _options;

    public SnapshotBackgroundService(
        IRecoveryManager recoveryManager,
        ISnapshotRepository snapshotRepo,
        ILogger<SnapshotBackgroundService> logger,
        IOptionsMonitor<WcsOptions> options)
    {
        _recoveryManager = recoveryManager;
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
                await _recoveryManager.SaveSnapshotAsync(stoppingToken);
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
