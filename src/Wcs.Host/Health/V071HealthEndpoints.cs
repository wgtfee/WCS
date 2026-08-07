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
            var snapshot = await EvaluateAsync(serviceName, checks, cancellationToken);
            return Results.Json(snapshot, statusCode: snapshot.ServiceStatus == ServiceStatus.Unavailable ? 503 : 200);
        });

        endpoints.MapGet("/health/traffic", async (HealthCheckService checks, CancellationToken cancellationToken) =>
        {
            var snapshot = await EvaluateAsync(serviceName, checks, cancellationToken);
            var traffic = HealthSnapshotEvaluator.ToTrafficHealth(snapshot);
            return Results.Json(traffic, statusCode: traffic.Status == TrafficStatus.Allowed ? 200 : 503);
        });
    }

    private static async Task<ServiceHealthSnapshot> EvaluateAsync(
        string serviceName,
        HealthCheckService checks,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var report = await checks.CheckHealthAsync(cancellationToken);
        var dependencies = report.Entries.Select(pair =>
        {
            var name = pair.Key;
            var entry = pair.Value;
            var readiness = name.Equals("readiness", StringComparison.OrdinalIgnoreCase)
                || entry.Tags.Any(tag => tag.Equals("readiness", StringComparison.OrdinalIgnoreCase));

            var dependencyStatus = entry.Status == HealthStatus.Healthy
                ? DependencyStatus.Healthy
                : DependencyStatus.Unhealthy;

            // A readiness warning means WCS is degraded but can still accept traffic.
            // Only an Unhealthy readiness result removes the instance from YARP traffic.
            var criticality = readiness
                ? entry.Status == HealthStatus.Degraded
                    ? DependencyCriticality.Degradable
                    : DependencyCriticality.Critical
                : DependencyCriticality.Optional;

            return new DependencyHealthItem(
                name,
                dependencyStatus,
                criticality,
                dependencyStatus == DependencyStatus.Healthy ? null : "WCS_DEPENDENCY_FAILED",
                dependencyStatus == DependencyStatus.Healthy ? null : entry.Description ?? "WCS 依赖检查失败",
                dependencyStatus == DependencyStatus.Healthy ? null : now,
                dependencyStatus == DependencyStatus.Healthy
                    ? null
                    : readiness
                        ? "WCS production readiness is affected"
                        : "Optional WCS capability is degraded",
                FallbackAvailable: criticality != DependencyCriticality.Critical);
        }).ToArray();

        return HealthSnapshotEvaluator.Evaluate(
            serviceName,
            Environment.MachineName,
            dependencies,
            checkedAt: now);
    }
}
