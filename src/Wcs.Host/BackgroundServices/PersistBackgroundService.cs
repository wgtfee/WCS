namespace Wcs.Host.BackgroundServices;

using System.Text.Json;
using SqlSugar;
using Microsoft.Extensions.Options;
using Wcs.Core.Common.Options;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 持久化后台服务 — 每 10s 将 StateCenter 快照写入 SqlSugar
/// 每次循环创建独立连接，不依赖 DI 单例（避免 Storageable + MARS 冲突）
/// </summary>
public class PersistBackgroundService : BackgroundService
{
    private readonly IStateCenter _stateCenter;
    private readonly string _connStr;
    private readonly ILogger<PersistBackgroundService> _logger;
    private readonly IOptionsMonitor<WcsOptions> _options;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PersistBackgroundService(
        IStateCenter stateCenter,
        IConfiguration config,
        ILogger<PersistBackgroundService> logger,
        IOptionsMonitor<WcsOptions> options)
    {
        _stateCenter = stateCenter;
        _connStr = config.GetConnectionString("WcsDb") ?? "";
        _logger = logger;
        _options = options;
    }

    private ISqlSugarClient CreateDb()
        => new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connStr,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.Persistence.IntervalSeconds;
        _logger.LogInformation("Persist service started (interval: {Interval}s)", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stateCenter.GetSnapshot();

                using var db = CreateDb();

                // 设备状态逐条 upsert
                foreach (var (deviceId, device) in snapshot.DeviceStates)
                {
                    var exists = await db.Queryable<DeviceRuntimeEntity>()
                        .Where(e => e.DeviceId == deviceId).AnyAsync(stoppingToken);
                    if (exists)
                        await db.Updateable(new DeviceRuntimeEntity
                        {
                            DeviceId = deviceId,
                            Status = device.Status.ToString(),
                            LastUpdateTime = device.LastUpdateTime,
                            Properties = device.Properties.Count > 0
                                ? JsonSerializer.Serialize(device.Properties, JsonOpts) : null
                        }).ExecuteCommandAsync(stoppingToken);
                    else
                        await db.Insertable(new DeviceRuntimeEntity
                        {
                            DeviceId = deviceId,
                            Status = device.Status.ToString(),
                            LastUpdateTime = device.LastUpdateTime,
                            Properties = device.Properties.Count > 0
                                ? JsonSerializer.Serialize(device.Properties, JsonOpts) : null
                        }).ExecuteCommandAsync(stoppingToken);
                }

                // 任务状态逐条 upsert
                foreach (var (taskId, task) in snapshot.TaskRuntimes)
                {
                    var exists = await db.Queryable<TaskRuntimeEntity>()
                        .Where(e => e.TaskId == taskId).AnyAsync(stoppingToken);
                    if (exists)
                        await db.Updateable(new TaskRuntimeEntity
                        {
                            TaskId = taskId,
                            Status = task.Status.ToString(),
                            Priority = task.Priority,
                            RouteId = task.RouteId,
                            StartTime = task.StartTime,
                            EndTime = task.EndTime,
                            Parameters = task.Parameters.Count > 0
                                ? JsonSerializer.Serialize(task.Parameters, JsonOpts) : null
                        }).ExecuteCommandAsync(stoppingToken);
                    else
                        await db.Insertable(new TaskRuntimeEntity
                        {
                            TaskId = taskId,
                            Status = task.Status.ToString(),
                            Priority = task.Priority,
                            RouteId = task.RouteId,
                            StartTime = task.StartTime,
                            EndTime = task.EndTime,
                            Parameters = task.Parameters.Count > 0
                                ? JsonSerializer.Serialize(task.Parameters, JsonOpts) : null
                        }).ExecuteCommandAsync(stoppingToken);
                }

                // 报警状态逐条 upsert
                foreach (var (alarmId, alarm) in snapshot.AlarmStates)
                {
                    var exists = await db.Queryable<AlarmRuntimeEntity>()
                        .Where(e => e.AlarmId == alarmId).AnyAsync(stoppingToken);
                    if (exists)
                        await db.Updateable(new AlarmRuntimeEntity
                        {
                            AlarmId = alarmId,
                            AlarmCode = alarm.AlarmCode,
                            Status = alarm.Status.ToString(),
                            Level = alarm.Level.ToString(),
                            Message = alarm.Message,
                            OccurTime = alarm.OccurTime,
                            RecoverTime = alarm.RecoverTime
                        }).ExecuteCommandAsync(stoppingToken);
                    else
                        await db.Insertable(new AlarmRuntimeEntity
                        {
                            AlarmId = alarmId,
                            AlarmCode = alarm.AlarmCode,
                            Status = alarm.Status.ToString(),
                            Level = alarm.Level.ToString(),
                            Message = alarm.Message,
                            OccurTime = alarm.OccurTime,
                            RecoverTime = alarm.RecoverTime
                        }).ExecuteCommandAsync(stoppingToken);
                }

                var total = snapshot.DeviceStates.Count + snapshot.TaskRuntimes.Count + snapshot.AlarmStates.Count;
                if (total > 0)
                    _logger.LogDebug("Persisted {Total} records", total);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.Persistence.IntervalSeconds), stoppingToken);
        }
    }
}
