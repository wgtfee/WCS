using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wcs.Core.Common.Options;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.Persistence.Repositories;

namespace Wcs.Host.BackgroundServices;

/// <summary>
/// 持久化后台服务 - 将 StateCenter 中的活跃数据持久化到 SQL Server
/// </summary>
public class PersistBackgroundService : BackgroundService
{
    private readonly IStateCenter _stateCenter;
    private readonly TaskRepository _taskRepo;
    private readonly AlarmRepository _alarmRepo;
    private readonly ILogger<PersistBackgroundService> _logger;
    private readonly IOptionsMonitor<WcsOptions> _options;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PersistBackgroundService(
        IStateCenter stateCenter,
        TaskRepository taskRepo,
        AlarmRepository alarmRepo,
        ILogger<PersistBackgroundService> logger,
        IOptionsMonitor<WcsOptions> options)
    {
        _stateCenter = stateCenter;
        _taskRepo = taskRepo;
        _alarmRepo = alarmRepo;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.Persistence.IntervalSeconds;
        _logger.LogInformation("Persist service started (interval: {Interval}s)", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _stateCenter.GetSnapshot();

                foreach (var (deviceId, device) in snapshot.DeviceStates)
                {
                    await _taskRepo.SaveDeviceRuntimeAsync(new DeviceRuntimeEntity
                    {
                        DeviceId = deviceId,
                        Status = device.Status.ToString(),
                        LastUpdateTime = device.LastUpdateTime,
                        Properties = device.Properties.Count > 0
                            ? JsonSerializer.Serialize(device.Properties, JsonOpts) : null
                    });
                }

                foreach (var (taskId, task) in snapshot.TaskRuntimes)
                {
                    await _taskRepo.SaveTaskRuntimeAsync(new TaskRuntimeEntity
                    {
                        TaskId = taskId,
                        Status = task.Status.ToString(),
                        Priority = task.Priority,
                        RouteId = task.RouteId,
                        StartTime = task.StartTime,
                        EndTime = task.EndTime,
                        Parameters = task.Parameters.Count > 0
                            ? JsonSerializer.Serialize(task.Parameters, JsonOpts) : null
                    });
                }

                foreach (var (alarmId, alarm) in snapshot.AlarmStates)
                {
                    await _alarmRepo.SaveAlarmRuntimeAsync(new AlarmRuntimeEntity
                    {
                        AlarmId = alarmId,
                        AlarmCode = alarm.AlarmCode,
                        Status = alarm.Status.ToString(),
                        Level = alarm.Level.ToString(),
                        Message = alarm.Message,
                        OccurTime = alarm.OccurTime,
                        RecoverTime = alarm.RecoverTime
                    });
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
