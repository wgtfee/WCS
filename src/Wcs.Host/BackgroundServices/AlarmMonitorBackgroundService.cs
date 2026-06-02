namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AlarmCenter;
using Wcs.Core.ResourceLock;

/// <summary>
/// 报警监控后台服务 - 定期检查系统健康状态
/// </summary>
public class AlarmMonitorBackgroundService : BackgroundService
{
    private readonly IAlarmCenter _alarmCenter;
    private readonly DeadlockDetector _deadlockDetector;
    private readonly ILogger<AlarmMonitorBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public AlarmMonitorBackgroundService(
        IAlarmCenter alarmCenter,
        DeadlockDetector deadlockDetector,
        ILogger<AlarmMonitorBackgroundService> logger,
        TimeSpan? interval = null)
    {
        _alarmCenter = alarmCenter;
        _deadlockDetector = deadlockDetector;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alarm monitor service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 死锁检测
                var deadlocks = _deadlockDetector.Detect();
                if (deadlocks.Count > 0)
                {
                    _logger.LogWarning("检测到 {Count} 个死锁", deadlocks.Count);
                    foreach (var dl in deadlocks)
                    {
                        await _alarmCenter.RaiseAlarmAsync(
                            "DEADLOCK",
                            Wcs.Core.StateCenter.Models.AlarmLevelEnum.Warning,
                            dl.ToString(),
                            "DeadlockDetector",
                            stoppingToken);
                    }

                    var resolved = _deadlockDetector.ResolveDeadlocks();
                    if (resolved > 0)
                    {
                        _logger.LogInformation("自动解除了 {Count} 个死锁", resolved);
                    }
                }

                // 报警统计
                var activeCount = _alarmCenter.GetActiveCount();
                if (activeCount > 0)
                {
                    _logger.LogWarning("当前活跃报警数: {Count}", activeCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alarm monitor cycle failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
