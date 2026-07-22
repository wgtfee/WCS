namespace Wcs.Application.HostedServices;

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AlarmCenter;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 将 PLC 驱动故障码转换为 AlarmCenter 报警，并在故障清除后走统一恢复管线。
/// </summary>
public sealed class TransportFaultAlarmHostedService : BackgroundService
{
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportFaultCatalogService _catalog;
    private readonly IAlarmCenter _alarms;
    private readonly ILogger<TransportFaultAlarmHostedService> _logger;
    private readonly ConcurrentDictionary<string, string> _activeAlarmCodes = new(StringComparer.Ordinal);

    public TransportFaultAlarmHostedService(
        ITransportPlcSignalMapRegistry maps,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportFaultCatalogService catalog,
        IAlarmCenter alarms,
        ILogger<TransportFaultAlarmHostedService> logger)
    {
        _maps = maps;
        _diagnostics = diagnostics;
        _catalog = catalog;
        _alarms = alarms;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 故障码与报警中心同步失败，本周期已跳过");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        foreach (var map in _maps.GetAll().Where(x => x.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_diagnostics.TryGet(map.VehicleId, out var diagnostic) || diagnostic is null)
                continue;

            if (diagnostic.FaultCode == 0)
            {
                if (_activeAlarmCodes.TryRemove(map.VehicleId, out var recoveredCode))
                    await _alarms.RecoverAlarmAsync(recoveredCode, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var definition = await _catalog.ResolveAsync(
                map.Kind,
                diagnostic.FaultCode,
                cancellationToken).ConfigureAwait(false);
            var logicalCode = definition?.AlarmCode ?? $"FAULT_{map.Kind}_{diagnostic.FaultCode}";
            var alarmCode = $"TRANSPORT_{map.VehicleId}_{logicalCode}";
            var message = definition?.Message
                ?? diagnostic.FaultMessage
                ?? $"{map.VehicleId} PLC 故障码 {diagnostic.FaultCode}";
            var level = definition?.Level ?? AlarmLevelEnum.Error;

            if (_activeAlarmCodes.TryGetValue(map.VehicleId, out var previousCode) &&
                !string.Equals(previousCode, alarmCode, StringComparison.Ordinal))
            {
                await _alarms.RecoverAlarmAsync(previousCode, cancellationToken).ConfigureAwait(false);
                _activeAlarmCodes.TryRemove(map.VehicleId, out _);
            }

            _alarms.SetAlarmRule(new AlarmRule
            {
                AlarmCode = alarmCode,
                Level = level,
                DelayRaiseMs = 300,
                DelayRecoverMs = 500,
                SuppressionWindowSec = 60,
                SuppressionThreshold = 20,
                AlarmGroup = $"TRANSPORT_{map.VehicleId}"
            });
            await _alarms.RaiseAlarmAsync(
                alarmCode,
                level,
                message,
                source: "TransportDriver",
                deviceId: map.VehicleId,
                alarmGroup: $"TRANSPORT_{map.VehicleId}",
                ct: cancellationToken).ConfigureAwait(false);
            _activeAlarmCodes[map.VehicleId] = alarmCode;
        }
    }
}

/// <summary>Host 启动后生成一次持久化状态与 PLC 状态的待处置冲突清单。</summary>
public sealed class TransportRecoveryConflictHostedService : IHostedService
{
    private readonly ITransportRecoveryConflictService _conflicts;
    private readonly ILogger<TransportRecoveryConflictHostedService> _logger;

    public TransportRecoveryConflictHostedService(
        ITransportRecoveryConflictService conflicts,
        ILogger<TransportRecoveryConflictHostedService> logger)
    {
        _conflicts = conflicts;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cases = await _conflicts.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var pending = cases.Count(x => x.State == TransportRecoveryConflictState.Pending);
        _logger.LogInformation("EMS/RGV 重启冲突清单已刷新，待人工处置 {PendingCount} 项", pending);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
