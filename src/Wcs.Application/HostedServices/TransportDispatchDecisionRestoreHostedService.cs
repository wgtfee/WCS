namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

/// <summary>在生产派单循环启动前恢复最近调度决策，供 Desktop 和现场审计回放。</summary>
public sealed class TransportDispatchDecisionRestoreHostedService : IHostedService
{
    private readonly JournalTransportDispatchDecisionStore _store;
    private readonly ILogger<TransportDispatchDecisionRestoreHostedService> _logger;

    public TransportDispatchDecisionRestoreHostedService(
        JournalTransportDispatchDecisionStore store,
        ILogger<TransportDispatchDecisionRestoreHostedService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "EMS/RGV 生产调度决策回放已恢复，当前保留 {DecisionCount} 条",
            _store.GetRecent(5000).Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
