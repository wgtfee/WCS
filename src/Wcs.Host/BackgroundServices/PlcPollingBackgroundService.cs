namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wcs.Core.Common.Options;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// PLC 轮询后台服务
/// </summary>
public class PlcPollingBackgroundService : BackgroundService
{
    private readonly IPlcPollingService _pollingService;
    private readonly IPlcBlockDiffEngine _diffEngine;
    private readonly IStateCenter _stateCenter;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PlcPollingBackgroundService> _logger;
    private readonly IOptionsMonitor<WcsOptions> _options;

    public PlcPollingBackgroundService(
        IPlcPollingService pollingService,
        IPlcBlockDiffEngine diffEngine,
        IStateCenter stateCenter,
        IEventBus eventBus,
        ILogger<PlcPollingBackgroundService> logger,
        IOptionsMonitor<WcsOptions> options)
    {
        _pollingService = pollingService;
        _diffEngine = diffEngine;
        _stateCenter = stateCenter;
        _eventBus = eventBus;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PLC polling service starting");

        try
        {
            await _pollingService.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = _options.CurrentValue.PlcPolling.IntervalMs;
                await Task.Delay(interval, stoppingToken);

                if (!_options.CurrentValue.PlcPolling.Enabled)
                    continue;

                // 注意：diff 与连接状态列表无关。
                // 原实现把块遍历嵌在 foreach(连接) 内，N 台 PLC 时每个块的
                // 变更事件会被重复发布 N 次；这里提升为单层循环。
                var cachedBlocks = _diffEngine.GetCachedBlocks();
                foreach (var cachedBlock in cachedBlocks)
                {
                    var lastBlock = _diffEngine.GetLastBlock(cachedBlock.PlcName, cachedBlock.BlockNumber);
                    if (lastBlock != null)
                    {
                        var diff = _diffEngine.ComparePlcBlocks(lastBlock, cachedBlock);
                        if (diff.HasChanges)
                        {
                            await _eventBus.PublishAsync(new PlcBlockChangedEvent
                            {
                                BlockName = diff.PlcName,
                                OldValues = new Dictionary<string, object> { ["Data"] = diff.OldData },
                                NewValues = new Dictionary<string, object> { ["Data"] = diff.NewData },
                                ChangedFields = diff.Changes.Select(c => $"Offset_{c.Offset}").ToList()
                            }, stoppingToken);
                        }
                    }
                    _diffEngine.SetLastBlock(cachedBlock);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _pollingService.StopAsync(CancellationToken.None);
            _logger.LogInformation("PLC polling service stopped");
        }
    }
}
