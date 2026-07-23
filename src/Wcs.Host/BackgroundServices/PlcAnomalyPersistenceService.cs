namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 把正式异常生命周期写入业务 SQL。异常候选和每个采样点不进业务表，
/// 仅激活/恢复状态发生变化时持久化，避免重新制造高频 SQL 压力。
/// </summary>
public sealed class PlcAnomalyPersistenceService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly string _connectionString;
    private readonly PlcAnomalyOptions _options;
    private readonly ILogger<PlcAnomalyPersistenceService> _logger;
    private readonly SemaphoreSlim _throttle = new(8, 8);

    public PlcAnomalyPersistenceService(
        IEventBus eventBus,
        IConfiguration configuration,
        PlcAnomalyOptions options,
        ILogger<PlcAnomalyPersistenceService> logger)
    {
        _eventBus = eventBus;
        _connectionString = configuration.GetConnectionString("WcsDb") ?? string.Empty;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        _eventBus.Subscribe<PlcAnomalyDetectedEvent>(async (evt, ct) =>
            await UpsertAsync(evt.Anomaly, ct));
        _eventBus.Subscribe<PlcAnomalyRecoveredEvent>(async (evt, ct) =>
            await UpsertAsync(evt.Anomaly, ct));

        _logger.LogInformation("PLC anomaly persistence service started");
        return Task.CompletedTask;
    }

    private async Task UpsertAsync(PlcAnomalyRecord anomaly, CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken);
        try
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var db = CreateDb();
                    var existing = await db.Queryable<PlcAnomalyEntity>()
                        .Where(item => item.AnomalyId == anomaly.AnomalyId)
                        .FirstAsync();
                    var entity = ToEntity(anomaly);
                    if (existing is null)
                        await db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
                    else
                        await db.Updateable(entity).ExecuteCommandAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
                }
            }

            _logger.LogError(
                lastError,
                "PLC anomaly persistence failed after retries: AnomalyId={AnomalyId}, Status={Status}",
                anomaly.AnomalyId,
                anomaly.Status);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private SqlSugarClient CreateDb() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static PlcAnomalyEntity ToEntity(PlcAnomalyRecord anomaly) => new()
    {
        AnomalyId = anomaly.AnomalyId,
        AnomalyKey = anomaly.AnomalyKey,
        AlarmCode = anomaly.AlarmCode,
        RuleId = anomaly.RuleId,
        Type = (int)anomaly.Type,
        Severity = (int)anomaly.Severity,
        Status = (int)anomaly.Status,
        PlcName = anomaly.PlcName,
        DbBlock = anomaly.DbBlock,
        DeviceId = anomaly.DeviceId,
        SignalName = anomaly.SignalName,
        DetectorName = anomaly.DetectorName,
        ModelVersion = anomaly.ModelVersion,
        Score = anomaly.Score,
        ActualValue = anomaly.ActualValue,
        ExpectedValue = anomaly.ExpectedValue,
        LowerBound = anomaly.LowerBound,
        UpperBound = anomaly.UpperBound,
        StartTimeUtc = anomaly.StartTimeUtc,
        LastSeenUtc = anomaly.LastSeenUtc,
        EndTimeUtc = anomaly.EndTimeUtc,
        Reason = anomaly.Reason,
        TaskId = anomaly.TaskId,
        RaiseAlarm = anomaly.RaiseAlarm,
        ContextJson = anomaly.ContextJson
    };
}
