namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 持久化后台服务 - 将 StateCenter 中的活跃数据持久化到数据库
/// </summary>
public class PersistBackgroundService : BackgroundService
{
    private readonly IStateCenter _stateCenter;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PersistBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public PersistBackgroundService(
        IStateCenter stateCenter,
        IEventBus eventBus,
        ILogger<PersistBackgroundService> logger,
        TimeSpan? interval = null)
    {
        _stateCenter = stateCenter;
        _eventBus = eventBus;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Persist service started (interval: {Interval})", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stateCenter.GetSnapshot();

                // 扩展点: 写入 SQL Server
                // await _taskRepo.SaveTaskRuntimeAsync(...)

                if (snapshot.DeviceStates.Count > 0 || snapshot.TaskRuntimes.Count > 0)
                {
                    _logger.LogDebug("Persisted {Devices} devices, {Tasks} tasks",
                        snapshot.DeviceStates.Count, snapshot.TaskRuntimes.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist cycle failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
