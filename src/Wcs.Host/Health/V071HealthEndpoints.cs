using Industrial.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Wcs.Host.Health;

public static class V071HealthEndpoints
{
    public static void MapV071Health(this IEndpointRouteBuilder endpoints, string serviceName)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new
        {
            service = serviceName,
            layer = "application",
            status = ServiceStatus.Healthy.ToString(),
            checkedAt = DateTimeOffset.UtcNow
        }));

        endpoints.MapGet("/health/dependencies", async (HealthCheckService checks, CancellationToken cancellationToken) =>
        {
            var report = await checks.CheckHealthAsync(cancellationToken);
            var dependencies = report.Entries.Select(pair =>
            {
                var name = pair.Key;
                var entry = pair.Value;
                var critical = entry.Tags.Any(tag => tag.Equals("readiness", StringComparison.OrdinalIgnoreCase));
                var status = entry.Status == HealthStatus.Healthy ? DependencyStatus.Healthy : DependencyStatus.Unhealthy;
                return new DependencyHealthItem(
                    name,
                    status,
                    critical ? DependencyCriticality.Critical : DependencyCriticality.Optional,
                    status == DependencyStatus.Healthy ? null : "WCS_DEPENDENCY_FAILED",
                    status == DependencyStatus.Healthy ? null : "WCS 依赖检查失败",
                    DateTimeOffset.UtcNow,
                    "恢复该依赖后重试");
            }).ToArray();
            var snapshot = HealthSnapshotEvaluator.Evaluate(serviceName, serviceName, dependencies, checkedAt: DateTimeOffset.UtcNow);
            return Results.Json(snapshot, statusCode: snapshot.ServiceStatus == ServiceStatus.Unavailable ? 503 : 200);
        });

        endpoints.MapGet("/health/traffic", async (HealthCheckService checks, CancellationToken cancellationToken) =>
        {
            var report = await checks.CheckHealthAsync(cancellationToken);
            var blocked = report.Entries.Values.Any(entry => entry.Tags.Any(tag => tag.Equals("readiness", StringComparison.OrdinalIgnoreCase)) && entry.Status != HealthStatus.Healthy);
            var reasons = blocked ? [new HealthReason("WCS_CRITICAL_DEPENDENCY_FAILED", "readiness", "WCS 关键依赖不可用", DateTimeOffset.UtcNow, "恢复数据库或关键依赖")] : Array.Empty<HealthReason>();
            var traffic = new TrafficHealth(blocked ? TrafficStatus.Blocked : TrafficStatus.Allowed,
                blocked ? ServiceStatus.Unavailable : ServiceStatus.Healthy,
                blocked ? "WCS_CRITICAL_DEPENDENCY_FAILED" : null,
                blocked ? "WCS 关键依赖不可用" : null,
                DateTimeOffset.UtcNow, reasons);
            return Results.Json(traffic, statusCode: blocked ? 503 : 200);
        });
    }
}
