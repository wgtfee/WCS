namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 业务事件持久化服务。
/// RawSignalEvent 已迁移到可配置的 PLC telemetry pipeline；
/// 本服务只保留必须进入业务 SQL 的事件。
/// </summary>
public class EventPersistenceService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly string _connStr;
    private readonly SemaphoreSlim _throttle = new(5, 10);
    private readonly ILogger<EventPersistenceService> _logger;

    public EventPersistenceService(IEventBus eventBus, IConfiguration config, ILogger<EventPersistenceService> logger)
    {
        _eventBus = eventBus;
        _connStr = config.GetConnectionString("WcsDb") ?? "";
        _logger = logger;
    }

    private ISqlSugarClient CreateDb()
        => new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connStr,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<PalletArrivedEvent>(async (evt, ct) =>
        {
            var entered = false;
            try
            {
                entered = await _throttle.WaitAsync(1000, ct);
                if (!entered) return;

                using var db = CreateDb();
                await db.Insertable(new DeviceStateLogEntity
                {
                    Id = SnowFlakeSingle.Instance.NextId(),
                    DeviceId = evt.DeviceId,
                    FieldName = "PalletArrived",
                    NewValue = "true",
                    ChangeTime = evt.OccurTime,
                    ValidatorPassed = true,
                    DomainEventType = nameof(PalletArrivedEvent),
                }).ExecuteCommandAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal publisher/Host shutdown.
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // SqlClient can translate cancellation into SqlException on shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写入 PalletArrivedEvent 失败");
            }
            finally
            {
                if (entered) _throttle.Release();
            }
        });

        _logger.LogInformation("EventPersistenceService 已启动（业务事件模式）");
        return Task.CompletedTask;
    }
}
