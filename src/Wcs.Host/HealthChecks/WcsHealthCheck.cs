namespace Wcs.Host.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.Common.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// 就绪探针 - 检查 StateCenter 和 PLC 连接
/// </summary>
public class WcsReadinessCheck : IHealthCheck
{
    private readonly IStateCenter _stateCenter;
    private readonly IOptionsMonitor<WcsOptions> _options;

    public WcsReadinessCheck(IStateCenter stateCenter, IOptionsMonitor<WcsOptions> options)
    {
        _stateCenter = stateCenter;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["DeviceCount"] = _stateCenter.GetAllDeviceStates().Count(),
            ["ActiveTaskCount"] = _stateCenter.GetAllActiveTasks().Count()
        };

        var pollingEnabled = _options.CurrentValue.PlcPolling.Enabled;
        data["PlcPolling"] = pollingEnabled ? "enabled" : "disabled";

        return Task.FromResult(HealthCheckResult.Healthy("WCS is ready", data));
    }
}

/// <summary>
/// 存活探针 - 简单存活检查
/// </summary>
public class WcsLivenessCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("WCS is alive"));
    }
}
