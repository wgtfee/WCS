namespace Wcs.Host.BackgroundServices;

using Wcs.Simulator.PlcSimulatorEngine;

public class SimulatorBackgroundService : BackgroundService
{
    private readonly SimulatedPlcPollingService _simulatedService;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<SimulatorBackgroundService> _logger;

    public SimulatorBackgroundService(
        SimulatedPlcPollingService simulatedService,
        IHostApplicationLifetime applicationLifetime,
        ILogger<SimulatorBackgroundService> logger)
    {
        _simulatedService = simulatedService;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Starting the simulator before Kestrel is listening lets PLC events,
        // SignalR fanout and SQL persistence compete with the host startup path.
        // Wait for the application-started signal so liveness is deterministic.
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = _applicationLifetime.ApplicationStarted.Register(
            () => started.TrySetResult());

        try
        {
            await started.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("========================================");
        _logger.LogInformation("  模拟 PLC 轮询服务启动");
        _logger.LogInformation("  3 PLC × 3 DB = 9 个模拟块");
        _logger.LogInformation("  18 个真实验证器全部就位");
        _logger.LogInformation("========================================");

        _simulatedService.RegisterDefaultValidators();
        _simulatedService.Start();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await _simulatedService.StopAsync();
        }
    }
}
