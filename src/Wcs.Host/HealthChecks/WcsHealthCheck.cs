namespace Wcs.Host.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.Common.Options;
using Microsoft.Extensions.Options;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 就绪探针 - 检查 StateCenter、PLC 轮询、EMS/RGV 健康评分和生产就绪报告。
/// </summary>
public class WcsReadinessCheck : IHealthCheck
{
    private readonly IStateCenter _stateCenter;
    private readonly IOptionsMonitor<WcsOptions> _options;
    private readonly ITransportObservabilityService _transport;
    private readonly ITransportResilienceService _resilience;

    public WcsReadinessCheck(
        IStateCenter stateCenter,
        IOptionsMonitor<WcsOptions> options,
        ITransportObservabilityService transport,
        ITransportResilienceService resilience)
    {
        _stateCenter = stateCenter;
        _options = options;
        _transport = transport;
        _resilience = resilience;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var transport = _transport.GetHealth();
        var readiness = _resilience.GetLastReadiness();
        var data = new Dictionary<string, object>
        {
            ["DeviceCount"] = _stateCenter.GetAllDeviceStates().Count(),
            ["ActiveTaskCount"] = _stateCenter.GetAllActiveTasks().Count(),
            ["TransportHealthState"] = transport.State.ToString(),
            ["TransportHealthScore"] = transport.Score,
            ["TransportEvaluatedAtUtc"] = transport.EvaluatedAtUtc,
            ["ProductionReadiness"] = readiness?.IsReady.ToString() ?? "not-evaluated",
            ["ReadinessCriticalCount"] = readiness?.CriticalCount ?? 0,
            ["ReadinessErrorCount"] = readiness?.ErrorCount ?? 0,
            ["ReadinessWarningCount"] = readiness?.WarningCount ?? 0
        };

        var pollingEnabled = _options.CurrentValue.PlcPolling.Enabled;
        data["PlcPolling"] = pollingEnabled ? "enabled" : "disabled";

        HealthCheckResult result;
        if (readiness is { CriticalCount: > 0 } || transport.State == TransportHealthState.Unhealthy)
        {
            result = HealthCheckResult.Unhealthy(
                $"WCS production readiness is unhealthy (transport={transport.Score}, critical={readiness?.CriticalCount ?? 0})",
                data: data);
        }
        else if (readiness is { ErrorCount: > 0 })
        {
            result = HealthCheckResult.Unhealthy(
                $"WCS production readiness has errors ({readiness.ErrorCount})",
                data: data);
        }
        else if (readiness is { WarningCount: > 0 } || transport.State == TransportHealthState.Degraded)
        {
            result = HealthCheckResult.Degraded(
                $"WCS production readiness is degraded (transport={transport.Score}, warning={readiness?.WarningCount ?? 0})",
                data: data);
        }
        else
        {
            result = HealthCheckResult.Healthy("WCS is ready", data);
        }
        return Task.FromResult(result);
    }
}

/// <summary>
/// 存活探针 - 简单存活检查。
/// </summary>
public class WcsLivenessCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("WCS is alive"));
    }
}
