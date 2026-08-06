using System.Diagnostics;
using System.Security.Claims;
using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Wcs.Host.IndustrialSecurity;

/// <summary>
/// The transport governance core already understands TransportPermissions. For human
/// management requests authenticated by IAM, project allowed canonical wcs.* capabilities
/// back into those existing claims immediately before controller execution. This adapter
/// is request-scoped and management-path-only; runtime workers never call IAM through it.
/// </summary>
public sealed class WcsManagementPermissionOverlayMiddleware(RequestDelegate next)
{
    private static readonly IReadOnlyDictionary<string, string> GovernanceCapabilities =
        WcsManagementPermissionCodes.CanonicalToTransport
            .Where(x => !string.Equals(x.Key, WcsManagementPermissionCodes.OperationManage, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(
        HttpContext context,
        IConfiguration configuration,
        IPermissionChecker permissionChecker,
        ILogger<WcsManagementPermissionOverlayMiddleware> logger)
    {
        if (!IsManagementRequest(context.Request)
            || !string.Equals(configuration["Security:Authentication:Mode"], "Centralized", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration["Security:Authorization:Mode"], "Centralized", StringComparison.OrdinalIgnoreCase)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var claims = new List<Claim>();
        foreach (var capability in GovernanceCapabilities)
        {
            if (await permissionChecker.HasPermissionAsync(capability.Key, context.RequestAborted))
                claims.Add(new Claim("permission", capability.Value));
        }

        if (claims.Count > 0)
        {
            context.User.AddIdentity(new ClaimsIdentity(
                claims,
                "WCS.IamGovernanceOverlay",
                ClaimTypes.Name,
                ClaimTypes.Role));
            logger.LogDebug(
                "Projected {Count} IAM WCS capabilities into transport governance claims. TraceId={TraceId}",
                claims.Count,
                context.TraceIdentifier);
        }

        await next(context);
    }

    private static bool IsManagementRequest(HttpRequest request)
        => request.Path.StartsWithSegments("/api/transport/administration", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Human mutation audit for the WCS management plane. It records identity, route, status
/// and duration only; request bodies are deliberately excluded to avoid leaking commands,
/// credentials or production data into logs. Existing TransportGovernance audit remains
/// the detailed domain audit for approved/executed operations.
/// </summary>
public sealed class WcsManagementAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        ILogger<WcsManagementAuditMiddleware> logger)
    {
        if (!IsAuditedMutation(context.Request))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogInformation(
                "WCS management mutation. GlobalUserId={GlobalUserId}; LocalUserId={LocalUserId}; UserName={UserName}; Method={Method}; Path={Path}; StatusCode={StatusCode}; TraceId={TraceId}; DurationMs={DurationMs:F2}",
                currentUser.GlobalUserId,
                currentUser.LocalUserId,
                currentUser.UserName,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                context.TraceIdentifier,
                durationMs);
        }
    }

    private static bool IsAuditedMutation(HttpRequest request)
        => request.Path.StartsWithSegments("/api/transport/administration", StringComparison.OrdinalIgnoreCase)
            && (HttpMethods.IsPost(request.Method)
                || HttpMethods.IsPut(request.Method)
                || HttpMethods.IsPatch(request.Method)
                || HttpMethods.IsDelete(request.Method));
}
