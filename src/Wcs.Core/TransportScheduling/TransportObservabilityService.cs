namespace Wcs.Core.TransportScheduling;

using System.Text.Json;
using Wcs.Core.AlarmCenter;
using Wcs.Core.StateCenter.Models;

public interface ITransportObservabilityService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportHealthSnapshot> EvaluateHealthAsync(CancellationToken cancellationToken = default);
    TransportHealthSnapshot GetHealth();
    TransportObservabilitySnapshot GetSnapshot();
}

public sealed class TransportObservabilityService : ITransportObservabilityService
{
    private const string HealthAlarmCode = "TRANSPORT_HEALTH";
    private readonly ITransportTelemetryService _telemetry;
    private readonly ITransportConsistencyInspectionService _consistency;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportExecutionEngine _executions;
    private readonly IRouteReservationManager _reservations;
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportDriverDiagnosticsService _drivers;
    private readonly IAlarmCenter _alarms;
    private readonly ITransportJournalStore _journal;
    private readonly TransportObservabilityOptions _options;
    private TransportHealthSnapshot _health = new()
    {
        State = TransportHealthState.Degraded,
        Score = 80,
        Components = new[]
        {
            new TransportHealthComponent
            {
                Component = "Startup",
                State = TransportHealthState.Degraded,
                Score = 80,
                Message = "等待首次健康评估"
            }
        }
    };

    public TransportObservabilityService(
        ITransportTelemetryService telemetry,
        ITransportConsistencyInspectionService consistency,
        ITransportVehicleRegistry vehicles,
        ITransportExecutionEngine executions,
        IRouteReservationManager reservations,
        ITransportProductionDispatchService production,
        ITransportDriverDiagnosticsService drivers,
        IAlarmCenter alarms,
        ITransportJournalStore journal,
        TransportObservabilityOptions options)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _consistency = consistency ?? throw new ArgumentNullException(nameof(consistency));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _production = production ?? throw new ArgumentNullException(nameof(production));
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ObservabilityHealth,
            20,
            cancellationToken).ConfigureAwait(false);
        var restored = records
            .Select(x => JsonSerializer.Deserialize<TransportHealthSnapshot>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportHealthSnapshot>()
            .OrderByDescending(x => x.EvaluatedAtUtc)
            .FirstOrDefault();
        if (restored is not null)
            Volatile.Write(ref _health, restored);
    }

    public async Task<TransportHealthSnapshot> EvaluateHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.HealthEvaluation,
            "transport.health.evaluate");
        try
        {
            var components = new List<TransportHealthComponent>
            {
                EvaluateConsistency(),
                EvaluateFleet(),
                EvaluateDrivers(),
                EvaluateQueue(),
                EvaluateAlarms()
            };
            var score = (int)Math.Round(components.Average(x => x.Score));
            var state = ResolveState(score);
            var snapshot = new TransportHealthSnapshot
            {
                State = state,
                Score = score,
                Components = components,
                EvaluatedAtUtc = DateTime.UtcNow
            };
            Volatile.Write(ref _health, snapshot);
            await _journal.UpsertAsync(new TransportJournalRecord
            {
                Category = TransportJournalCategory.ObservabilityHealth,
                RecordId = snapshot.EvaluatedAtUtc.ToString("yyyyMMddHHmmssfff"),
                PayloadJson = JsonSerializer.Serialize(snapshot),
                OccurredAtUtc = snapshot.EvaluatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
            await UpdateAlarmAsync(snapshot, cancellationToken).ConfigureAwait(false);
            operation.Complete(
                state != TransportHealthState.Unhealthy,
                $"运输系统健康分 {score}，状态 {state}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["health.score"] = score.ToString(),
                    ["health.state"] = state.ToString()
                });
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
    }

    public TransportHealthSnapshot GetHealth() => Volatile.Read(ref _health);

    public TransportObservabilitySnapshot GetSnapshot()
    {
        var vehicles = _vehicles.GetAll();
        var executions = _executions.GetAll();
        return new TransportObservabilitySnapshot
        {
            Health = GetHealth(),
            Metrics = _telemetry.GetMetricsSnapshot(),
            LastConsistencyReport = _consistency.GetLastReport(),
            OnlineVehicleCount = vehicles.Count(x => x.IsOnline),
            OfflineVehicleCount = vehicles.Count(x => !x.IsOnline),
            ActiveExecutionCount = executions.Count(x => !x.IsTerminal),
            QueueLength = _production.GetQueue().Count(x => x.State is not (
                TransportProductionQueueState.Assigned or
                TransportProductionQueueState.Cancelled)),
            ActiveReservationCount = _reservations.GetActiveReservations().Count,
            ActiveAlarmCount = _alarms.GetActiveCount()
        };
    }

    private TransportHealthComponent EvaluateConsistency()
    {
        var report = _consistency.GetLastReport();
        if (report is null)
            return Component("Consistency", 80, "尚未执行三方一致性巡检");
        if (!report.Success)
            return Component("Consistency", 20, $"巡检失败：{report.Error}");
        var score = Math.Clamp(
            100 - report.CriticalCount * 35 - report.ErrorCount * 15 - report.WarningCount * 5,
            0,
            100);
        return Component(
            "Consistency",
            score,
            report.IsConsistent ? "数据库、内存和 PLC 状态一致" : $"存在 {report.Issues.Count} 项差异");
    }

    private TransportHealthComponent EvaluateFleet()
    {
        var vehicles = _vehicles.GetAll();
        if (vehicles.Count == 0)
            return Component("Fleet", 100, "尚未配置运输车辆");
        var online = vehicles.Count(x => x.IsOnline);
        var faulted = vehicles.Count(x => x.State == TransportVehicleOperatingState.Faulted);
        var score = Math.Clamp((int)Math.Round(online * 100d / vehicles.Count) - faulted * 20, 0, 100);
        return Component("Fleet", score, $"在线 {online}/{vehicles.Count}，故障 {faulted}");
    }

    private TransportHealthComponent EvaluateDrivers()
    {
        var drivers = _drivers.GetAll().Where(x => x.Mode == TransportDriverMode.PlcTag).ToArray();
        if (drivers.Length == 0)
            return Component("PLC", 100, "当前为模拟模式或尚未配置真实 PLC 驱动");
        var healthy = drivers.Count(x => x.AccessorConnected && x.DeviceOnline && x.ConsecutiveReadFailures == 0);
        var stale = drivers.Count(x => x.LastReadAtUtc.HasValue && DateTime.UtcNow - x.LastReadAtUtc.Value > TimeSpan.FromSeconds(10));
        var score = Math.Clamp((int)Math.Round(healthy * 100d / drivers.Length) - stale * 10, 0, 100);
        return Component("PLC", score, $"健康驱动 {healthy}/{drivers.Length}，读取超时 {stale}");
    }

    private TransportHealthComponent EvaluateQueue()
    {
        var queue = _production.GetQueue()
            .Where(x => x.State is not (TransportProductionQueueState.Assigned or TransportProductionQueueState.Cancelled))
            .ToArray();
        if (queue.Length == 0)
            return Component("Queue", 100, "生产运输队列无等待任务");
        var oldestSeconds = queue.Max(x => Math.Max(0, (DateTime.UtcNow - x.ProductionRequest.EnqueuedAtUtc).TotalSeconds));
        var failed = queue.Count(x => x.State == TransportProductionQueueState.Failed);
        var score = oldestSeconds switch
        {
            > 600 => 35,
            > 300 => 55,
            > 60 => 80,
            _ => 95
        };
        score = Math.Clamp(score - failed * 10, 0, 100);
        return Component("Queue", score, $"等待 {queue.Length}，最长 {Math.Round(oldestSeconds)} 秒，失败 {failed}");
    }

    private TransportHealthComponent EvaluateAlarms()
    {
        var alarms = _alarms.GetActiveAlarms().ToArray();
        var critical = alarms.Count(x => x.Level == AlarmLevelEnum.Critical);
        var error = alarms.Count(x => x.Level == AlarmLevelEnum.Error);
        var warning = alarms.Count(x => x.Level == AlarmLevelEnum.Warning);
        var score = critical > 0 ? 20 : error > 0 ? 50 : warning > 0 ? 80 : 100;
        return Component("Alarm", score, $"Critical {critical}，Error {error}，Warning {warning}");
    }

    private TransportHealthComponent Component(string name, int score, string message) => new()
    {
        Component = name,
        Score = score,
        State = ResolveState(score),
        Message = message
    };

    private TransportHealthState ResolveState(int score)
    {
        if (score < _options.UnhealthyScoreThreshold)
            return TransportHealthState.Unhealthy;
        if (score < _options.DegradedScoreThreshold)
            return TransportHealthState.Degraded;
        return TransportHealthState.Healthy;
    }

    private async Task UpdateAlarmAsync(
        TransportHealthSnapshot health,
        CancellationToken cancellationToken)
    {
        if (health.State == TransportHealthState.Healthy)
        {
            await _alarms.RecoverAlarmAsync(HealthAlarmCode, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _alarms.RaiseAlarmAsync(
            HealthAlarmCode,
            health.State == TransportHealthState.Unhealthy
                ? AlarmLevelEnum.Critical
                : AlarmLevelEnum.Warning,
            $"EMS/RGV 运输系统健康分 {health.Score}，状态 {health.State}",
            source: "TransportObservability",
            alarmGroup: "TransportObservability",
            ct: cancellationToken).ConfigureAwait(false);
    }
}
