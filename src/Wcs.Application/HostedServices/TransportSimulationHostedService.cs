namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

public sealed class TransportSimulationInitializationHostedService : IHostedService
{
    private readonly ITransportSimulationService _service;
    private readonly ILogger<TransportSimulationInitializationHostedService> _logger;

    public TransportSimulationInitializationHostedService(
        ITransportSimulationService service,
        ILogger<TransportSimulationInitializationHostedService> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _service.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("EMS/RGV 仿真、策略对比、容量基线和最终验收历史加载完成");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
