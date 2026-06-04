namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 事件持久化服务 — 订阅 EventBus 中的关键事件，写入 SqlSugar 业务表
/// 让数据库能实时看到系统运行数据
/// </summary>
public class EventPersistenceService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly ISqlSugarClient _db;
    private readonly ILogger<EventPersistenceService> _logger;

    public EventPersistenceService(IEventBus eventBus, ISqlSugarClient db, ILogger<EventPersistenceService> logger)
    {
        _eventBus = eventBus;
        _db = db;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 订阅 RawSignalEvent → 写入 Wcs_DeviceStateLog
        _eventBus.Subscribe<RawSignalEvent>(async (evt, ct) =>
        {
            try
            {
                await _db.Insertable(new DeviceStateLogEntity
                {
                    Id = DateTime.UtcNow.Ticks,
                    DeviceId = ExtractDeviceId(evt.FieldName) ?? evt.FieldName,
                    FieldName = evt.FieldName,
                    OldValue = evt.OldValue,
                    NewValue = evt.NewValue,
                    ChangeTime = evt.OccurTime,
                    PlcName = evt.PlcName,
                    DbBlock = evt.DbBlock,
                    ValidatorPassed = evt.ValidatorPassed,
                    ValidatorReason = evt.ValidatorReason
                }).ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "写入 DeviceStateLog 失败");
            }
        });

        // 订阅 PalletArrivedEvent → 写入 Wcs_DeviceStateLog（标记为业务事件）
        _eventBus.Subscribe<PalletArrivedEvent>(async (evt, ct) =>
        {
            try
            {
                await _db.Insertable(new DeviceStateLogEntity
                {
                    Id = DateTime.UtcNow.Ticks,
                    DeviceId = evt.DeviceId,
                    FieldName = "PalletArrived",
                    NewValue = "true",
                    ChangeTime = evt.OccurTime,
                    ValidatorPassed = true,
                    DomainEventType = nameof(PalletArrivedEvent)
                }).ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "写入 PalletArrivedEvent 失败");
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
