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

                // 设备状态 upsert：先 UPDATE 未命中再 INSERT（避免与归档/其他写入方主键冲突）
                foreach (var (deviceId, device) in snapshot.DeviceStates)
                {
                    var entity = new DeviceRuntimeEntity
                    {
                        DeviceId = deviceId,
                        Status = device.Status.ToString(),
                        LastUpdateTime = device.LastUpdateTime,
                        Properties = device.Properties.Count > 0 ? JsonSerializer.Serialize(device.Properties, JsonOpts) : null
                    };
                    var updated = await db.Updateable(entity)
                        .WhereColumns(x => x.DeviceId).ExecuteCommandAsync(stoppingToken);
                    if (updated == 0)
                        await db.Insertable(entity).ExecuteCommandAsync(stoppingToken);
                }

                // 任务状态 upsert
                foreach (var (taskId, task) in snapshot.TaskRuntimes)
                {
                    var entity = new TaskRuntimeEntity
                    {
                        TaskId = taskId,
                        Status = task.Status.ToString(),
                        Priority = task.Priority,
                        RouteId = task.RouteId,
                        StartTime = task.StartTime,
                        EndTime = task.EndTime,
                        Parameters = task.Parameters.Count > 0 ? JsonSerializer.Serialize(task.Parameters, JsonOpts) : null
                    };
                    var updated = await db.Updateable(entity)
                        .WhereColumns(x => x.TaskId).ExecuteCommandAsync(stoppingToken);
                    if (updated == 0)
                        await db.Insertable(entity).ExecuteCommandAsync(stoppingToken);
                }

                // 报警状态 upsert
                foreach (var (alarmId, alarm) in snapshot.AlarmStates)
                {
                    var entity = new AlarmRuntimeEntity
                    {
                        AlarmId = alarmId,
                        AlarmCode = alarm.AlarmCode,
                        Status = alarm.Status.ToString(),
                        Level = alarm.Level.ToString(),
                        Message = alarm.Message,
                        OccurTime = alarm.OccurTime,
                        RecoverTime = alarm.RecoverTime
                    };
                    var updated = await db.Updateable(entity)
                        .WhereColumns(x => x.AlarmId).ExecuteCommandAsync(stoppingToken);
                    if (updated == 0)
                        await db.Insertable(entity).ExecuteCommandAsync(stoppingToken);
                }

                var total = snapshot.DeviceStates.Count + snapshot.TaskRuntimes.Count + snapshot.AlarmStates.Count;
                if (total > 0)
                    _logger.LogDebug("Persisted {Total} records", total);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // SqlClient may surface a cancelled command as SqlException rather than
                // OperationCanceledException during graceful Host shutdown.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist cycle failed");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.CurrentValue.Persistence.IntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
