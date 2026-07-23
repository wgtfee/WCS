namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 事件持久化服务 — 订阅 EventBus 关键事件写入 SqlSugar
///
/// 重要：不共享 DI 单例 ISqlSugarClient。
/// 使用独立短连接 + 连接池（Max Pool Size=200），
/// 避免并发写入争用同一个连接导致的 MARS/Connecting 异常。
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

    /// <summary>独立连接，不共享 DI 单例</summary>
    private ISqlSugarClient CreateDb()
        => new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connStr,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<RawSignalEvent>(async (evt, ct) =>
        {
            if (!await _throttle.WaitAsync(1000, ct)) return;
            try
            {
                using var db = CreateDb();
                await db.Insertable(new DeviceStateLogEntity
                {
                    Id = SnowFlakeSingle.Instance.NextId(),
                    DeviceId = ExtractDeviceId(evt.FieldName) ?? evt.FieldName,
                    FieldName = evt.FieldName,
                    OldValue = evt.OldValue,
                    NewValue = evt.NewValue,
                    ChangeTime = evt.OccurTime,
                    PlcName = evt.PlcName,
                    DbBlock = evt.DbBlock,
                    ValidatorPassed = evt.ValidatorPassed,
                    ValidatorReason = evt.ValidatorReason ?? "",
                    DomainEventType = evt.DomainEventType ?? ""
                }).ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写入 DeviceStateLog 失败");
            }
            finally { _throttle.Release(); }
        });

        _eventBus.Subscribe<PalletArrivedEvent>(async (evt, ct) =>
        {
            if (!await _throttle.WaitAsync(1000, ct)) return;
            try
            {
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写入 PalletArrivedEvent 失败");
            }
            finally { _throttle.Release(); }
        });


        _logger.LogInformation("EventPersistenceService 已启动");
        return Task.CompletedTask;
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }
}
