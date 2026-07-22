namespace Wcs.Application.HostedServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 将执行状态机生成的协议无关命令自动交给可靠命令分发器。
/// 命令失败后不重新塞回执行队列，避免 Move/Load/Unload 在物理结果未知时重复执行；
/// 持久化命令记录由第三阶段状态存储和第八阶段补偿流程继续处理。
/// </summary>
public sealed class TransportExecutionCommandPumpHostedService : BackgroundService
{
    private readonly ITransportExecutionEngine _executions;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportCommandDispatcher _dispatcher;
    private readonly ILogger<TransportExecutionCommandPumpHostedService> _logger;

    public TransportExecutionCommandPumpHostedService(
        ITransportExecutionEngine executions,
        ITransportVehicleRegistry vehicles,
        ITransportCommandDispatcher dispatcher,
        ILogger<TransportExecutionCommandPumpHostedService> logger)
    {
        _executions = executions;
        _vehicles = vehicles;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 执行命令泵本周期失败，Host 继续运行");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        foreach (var vehicle in _vehicles.GetAll().Where(x => x.IsOnline))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commands = _executions.DequeueCommands(vehicle.VehicleId, 20);
            foreach (var command in commands)
            {
                var result = await _dispatcher.DispatchAsync(
                    command,
                    vehicle.Kind,
                    maxRetries: 2,
                    cancellationToken).ConfigureAwait(false);
                if (result.Status is not (
                    TransportCommandStatus.Acknowledged or
                    TransportCommandStatus.Completed))
                {
                    _logger.LogWarning(
                        "EMS/RGV 执行命令下发未成功，车辆 {VehicleId}，命令 {CommandId}，状态 {Status}，错误 {Error}",
                        vehicle.VehicleId,
                        command.CommandId,
                        result.Status,
                        result.Error);
                }
            }
        }
    }
}
