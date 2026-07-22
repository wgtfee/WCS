namespace Wcs.Host.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.Common.Options;
using Microsoft.Extensions.Options;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 就绪探针 - 检查 StateCenter、PLC 轮询和 EMS/RGV 生产健康评分。
/// </summary>
public class WcsReadinessCheck : IHealthCheck
{
    private readonly IStateCenter _stateCenter;
    private readonly IOptionsMonitor<WcsOptions> _options;
    private readonly ITransportObservabilityService _transport;

    public WcsReadinessCheck(
        IStateCenter stateCenter,
        IOptionsMonitor<WcsOptions> options,
        ITransportObservabilityService transport)
    {
        _stateCenter = stateCenter;
        _options = options;
        _transport = transport;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var transport = _transport.GetHealth();
        var data = new Dictionary<string, object>
        {
            ["DeviceCount"] = _stateCenter.GetAllDeviceStates().Count(),
            ["ActiveTaskCount"] = _stateCenter.GetAllActiveTasks().Count(),
            ["TransportHealthState"] = transport.State.ToString(),
            ["TransportHealthScore"] = transport.Score,
            ["TransportEvaluatedAtUtc"] = transport.EvaluatedAtUtc
        };

        var pollingEnabled = _options.CurrentValue.PlcPolling.Enabled;
        data["PlcPolling"] = pollingEnabled ? "enabled" : "disabled";

        var result = transport.State switch
        {
            TransportHealthState.Unhealthy => HealthCheckResult.Unhealthy(
                $"WCS transport health is unhealthy ({transport.Score})",
                data: data),
            TransportHealthState.Degraded => HealthCheckResult.Degraded(
                $"WCS transport health is degraded ({transport.Score})",
                data: data),
            _ => HealthCheckResult.Healthy("WCS is ready", data)
        };
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
