using Industrial.Security.Abstractions;

namespace Wcs.Host.Middleware;

/// <summary>
/// Fail-closed baseline for every human-facing WCS controller. Existing fine-grained
/// Permission attributes remain additive for dangerous operations.
/// </summary>
public sealed class WcsApiPermissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPermissionChecker checker)
    {
        if (!context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/api/security"))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var permission = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            ? Wcs.Host.IndustrialSecurity.WcsManagementPermissionCodes.RuntimeView
            : Wcs.Host.IndustrialSecurity.WcsManagementPermissionCodes.RuntimeOperate;
        if (!await checker.HasPermissionAsync(permission, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "permission_denied",
                permission
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }
}
