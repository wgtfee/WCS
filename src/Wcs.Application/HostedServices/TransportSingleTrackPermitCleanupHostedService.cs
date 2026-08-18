namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 对已完成或已取消任务重复尝试释放单轨逻辑许可。
/// 如果交通资源仍标记 OccupancyConfirmed，协调器会拒绝释放；清场后下一周期自动释放。
/// </summary>
public sealed class TransportSingleTrackPermitCleanupHostedService : BackgroundService
{
    private readonly ITransportExecutionEngine _executions;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ILogger<TransportSingleTrackPermitCleanupHostedService> _logger;

    public TransportSingleTrackPermitCleanupHostedService(
        ITransportExecutionEngine executions,
        ITransportSingleTrackCoordinator singleTrack,
        ILogger<TransportSingleTrackPermitCleanupHostedService> logger)
    {
        _executions = executions;
        _singleTrack = singleTrack;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var execution in _executions.GetAll().Where(x => x.IsTerminal))
                    _singleTrack.Release(execution.RequestId, requirePhysicalClearance: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 单轨终态许可清理失败，本周期已跳过");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
