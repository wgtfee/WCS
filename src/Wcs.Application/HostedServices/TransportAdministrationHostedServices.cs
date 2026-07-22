namespace Wcs.Application.HostedServices;

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

public sealed class TransportConfigurationHostedService : IHostedService
{
    private readonly ITransportConfigurationService _configuration;
    private readonly ILogger<TransportConfigurationHostedService> _logger;

    public TransportConfigurationHostedService(
        ITransportConfigurationService configuration,
        ILogger<TransportConfigurationHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.Version <= 0)
        {
            _logger.LogInformation("EMS/RGV 调度配置尚未持久化，继续使用代码默认配置");
            return;
        }

        _configuration.Apply(configuration);
        _logger.LogInformation(
            "EMS/RGV 调度配置已加载，版本 {Version}，交通资源 {ResourceCount}，充电站 {StationCount}，车辆 {VehicleCount}",
            configuration.Version,
            configuration.TrafficResources.Count,
            configuration.ChargingStations.Count,
            configuration.Vehicles.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TransportJournalHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITransportJournalStore _journal;
    private readonly ITransportChargingCoordinator _charging;
    private readonly ITransportTaskReassignmentService _reassignments;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportPerformanceService _performance;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ILogger<TransportJournalHostedService> _logger;

    public TransportJournalHostedService(
        ITransportJournalStore journal,
        ITransportChargingCoordinator charging,
        ITransportTaskReassignmentService reassignments,
        ITransportTrafficCoordinator traffic,
        ITransportPerformanceService performance,
        ITransportVehicleRegistry vehicles,
        ILogger<TransportJournalHostedService> logger)
    {
        _journal = journal;
        _charging = charging;
        _reassignments = reassignments;
        _traffic = traffic;
        _performance = performance;
        _vehicles = vehicles;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PersistAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 运行日志持久化失败，本次快照已跳过");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        foreach (var plan in _charging.GetPlans())
        {
            await UpsertAsync(
                TransportJournalCategory.ChargingPlan,
                plan.PlanId,
                plan,
                plan.UpdatedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var reassignment in _reassignments.GetHistory())
        {
            await UpsertAsync(
                TransportJournalCategory.TaskReassignment,
                reassignment.ReassignmentId,
                reassignment,
                reassignment.OccurredAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var incident in _traffic.GetIncidents())
        {
            await UpsertAsync(
                TransportJournalCategory.TrafficIncident,
                incident.IncidentId,
                incident,
                incident.OccurredAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        var performance = _performance.GetSnapshot();
        await UpsertAsync(
            TransportJournalCategory.PerformanceSnapshot,
            performance.GeneratedAtUtc.ToString("yyyyMMddHHmm"),
            performance,
            performance.GeneratedAtUtc,
            cancellationToken).ConfigureAwait(false);

        foreach (var vehicle in _vehicles.GetAll())
        {
            await UpsertAsync(
                TransportJournalCategory.DriverState,
                vehicle.VehicleId,
                vehicle,
                vehicle.UpdatedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task UpsertAsync<T>(
        TransportJournalCategory category,
        string recordId,
        T value,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken) =>
        _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = category,
            RecordId = recordId,
            PayloadJson = JsonSerializer.Serialize(value, JsonOptions),
            OccurredAtUtc = occurredAtUtc,
            UpdatedAtUtc = DateTime.UtcNow
        }, cancellationToken);
}
