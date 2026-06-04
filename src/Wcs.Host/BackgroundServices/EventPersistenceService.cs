namespace Wcs.Host.BackgroundServices;

using Microsoft.Extensions.Configuration;
using SqlSugar;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 事件持久化服务 — 订阅 EventBus 中的关键事件，写入 SqlSugar 业务表
/// 每次写入创建独立连接（避免 singleton ISqlSugarClient 并发冲突）
/// </summary>
public class EventPersistenceService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly string _connectionString;
    private readonly ILogger<EventPersistenceService> _logger;

    public EventPersistenceService(IEventBus eventBus, IConfiguration config, ILogger<EventPersistenceService> logger)
    {
        _eventBus = eventBus;
        _connectionString = config.GetConnectionString("WcsDb") ?? "";
        _logger = logger;
    }

    /// <summary>每次写入创建独立 SqlSugarClient（避免 singleton 并发连接冲突）</summary>
    private ISqlSugarClient CreateDb()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<RawSignalEvent>(async (evt, ct) =>
        {
            try
            {
                using var db = CreateDb();
                await db.Insertable(new DeviceStateLogEntity
                {
                    Id = DateTime.UtcNow.Ticks + Random.Shared.Next(0, 9999),
                    DeviceId = ExtractDeviceId(evt.FieldName) ?? evt.FieldName,
                    FieldName = evt.FieldName,
                    OldValue = evt.OldValue,
                    NewValue = evt.NewValue,
                    ChangeTime = evt.OccurTime,
                    PlcName = evt.PlcName,
                    DbBlock = evt.DbBlock,
                    ValidatorPassed = evt.ValidatorPassed,
                    ValidatorReason = evt.ValidatorReason,
                    DomainEventType = evt.DomainEventType ?? ""
                }).ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "写入 DeviceStateLog 失败");
            }
        });

        _eventBus.Subscribe<PalletArrivedEvent>(async (evt, ct) =>
        {
            try
            {
                using var db = CreateDb();
                await db.Insertable(new DeviceStateLogEntity
                {
                    Id = DateTime.UtcNow.Ticks + Random.Shared.Next(0, 9999),
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
