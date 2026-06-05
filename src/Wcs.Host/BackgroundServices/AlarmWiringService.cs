namespace Wcs.Host.BackgroundServices;

using SqlSugar;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 报警接线服务 — 将 EventBus 中的 DeviceFaultEvent 接入 AlarmCenter 管线
///
/// 完整报警数据流：
///   EventDetector 检测到 _Fault 上升沿
///   → DeviceFaultEvent → 本服务收到
///   → AlarmCenter.RaiseAlarmAsync() → 5 层报警管线
///   → StateCenter 更新报警状态
///   → PersistBackgroundService 每 10s 写入 Wcs_AlarmRuntime
///   → AlarmRaisedEvent → EventPersistenceService 写入 Wcs_DeviceStateLog
/// </summary>
public class AlarmWiringService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly IAlarmCenter _alarmCenter;
    private readonly ISqlSugarClient? _db;
    private readonly string _connStr;
    private readonly ILogger<AlarmWiringService> _logger;

    public AlarmWiringService(
        IEventBus eventBus,
        IAlarmCenter alarmCenter,
        ISqlSugarClient? db,
        IConfiguration config,
        ILogger<AlarmWiringService> logger)
    {
        _eventBus = eventBus;
        _alarmCenter = alarmCenter;
        _db = db;
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
        // DeviceFaultEvent → AlarmCenter 报警管线
        _eventBus.Subscribe<DeviceFaultEvent>(async (evt, ct) =>
        {
            _logger.LogWarning("[Alarm] ⚠ {Device} 故障: {FaultCode}", evt.DeviceId, evt.FaultCode);

            try
            {
                await _alarmCenter.RaiseAlarmAsync(
                    alarmCode: evt.FaultCode,
                    level: Wcs.Core.StateCenter.Models.AlarmLevelEnum.Error,
                    message: evt.Description ?? $"Device {evt.DeviceId} fault: {evt.FaultCode}",
                    deviceId: evt.DeviceId,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Alarm] RaiseAlarmAsync 失败");
            }
        });

        _eventBus.Subscribe<DeviceRecoveredEvent>(async (evt, ct) =>
        {
            _logger.LogWarning("[Alarm] ✅ {Device} 恢复: {FaultCode}", evt.DeviceId, evt.FaultCode);

            try
            {
                await _alarmCenter.RecoverAlarmAsync(alarmCode: evt.FaultCode,ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Alarm] RecoverAlarmAsync 失败");
            }
        });

        // AlarmRaisedEvent → 写入 Wcs_DeviceStateLog + Wcs_AlarmRuntime
        _eventBus.Subscribe<AlarmRaisedEvent>(async (evt, ct) =>
        {
            try
            {
                using var db = CreateDb();
                await db.Insertable(new DeviceStateLogEntity
                {
                    Id = DateTime.UtcNow.Ticks + Random.Shared.Next(0, 9999),
                    DeviceId = evt.AlarmCode,
                    FieldName = $"Alarm_{evt.AlarmCode}",
                    NewValue = "raised",
                    ChangeTime = evt.OccurTime,
                    ValidatorPassed = true,
                    DomainEventType = nameof(AlarmRaisedEvent)
                }).ExecuteCommandAsync(ct);

                // 也写入 Wcs_AlarmRuntime（作为运行时记录）
                await db.Insertable(new AlarmRuntimeEntity
                {
                    AlarmId = evt.AlarmId,
                    AlarmCode = evt.AlarmCode,
                    Status = "Active",
                    Level = evt.Level.ToString(),
                    Message = evt.Message,
                    OccurTime = evt.OccurTime
                }).ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Alarm] 写入报警事件失败");
            }
        });

        // AlarmRecoveredEvent → Wcs_AlarmHistory + 更新 Wcs_AlarmRuntime
        _eventBus.Subscribe<AlarmRecoveredEvent>(async (evt, ct) =>
        {
            try
            {
                using var db = CreateDb();

                // 写入历史
                await db.Insertable(new AlarmHistoryEntity
                {
                    AlarmCode = evt.AlarmCode,
                    Level = "Info",
                    Message = $"Alarm {evt.AlarmCode} recovered at {evt.RecoverTime:O}",
                    StartTime = evt.OccurTime,
                    EndTime = evt.RecoverTime
                }).ExecuteCommandAsync(ct);

                // 删除运行时记录
                await db.Deleteable<AlarmRuntimeEntity>()
                    .Where(a => a.AlarmCode == evt.AlarmCode && a.Status == "Active")
                    .ExecuteCommandAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Alarm] 写入恢复事件失败");
            }
        });

        _logger.LogInformation("AlarmWiringService 已启动 — DeviceFaultEvent → AlarmCenter → DB");
        return Task.CompletedTask;
    }
}
