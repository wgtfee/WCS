using System.Security.Claims;
using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Http;
using Wcs.Host.IndustrialSecurity;
using Wcs.Host.Middleware;

namespace Wcs.Host.Tests.IndustrialSecurity;

public sealed class WcsApiPermissionMiddlewareTests
{
    [Fact]
    public async Task AnonymousApiRequest_IsRejectedBeforePermissionLookup()
    {
        var called = false;
        var middleware = new WcsApiPermissionMiddleware(_ => { called = true; return Task.CompletedTask; });
        var checker = new StubPermissionChecker(true);
        var context = NewContext(HttpMethods.Get, authenticated: false);

        await middleware.InvokeAsync(context, checker);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(called);
        Assert.Null(checker.LastPermission);
    }

    [Fact]
    public async Task GetApiRequest_RequiresRuntimeView()
    {
        var called = false;
        var middleware = new WcsApiPermissionMiddleware(_ => { called = true; return Task.CompletedTask; });
        var checker = new StubPermissionChecker(true);
        var context = NewContext(HttpMethods.Get, authenticated: true);

        await middleware.InvokeAsync(context, checker);

        Assert.True(called);
        Assert.Equal(WcsManagementPermissionCodes.RuntimeView, checker.LastPermission);
    }

    [Fact]
    public async Task MutatingApiRequest_RequiresRuntimeOperateAndFailsClosed()
    {
        var called = false;
        var middleware = new WcsApiPermissionMiddleware(_ => { called = true; return Task.CompletedTask; });
        var checker = new StubPermissionChecker(false);
        var context = NewContext(HttpMethods.Post, authenticated: true);

        await middleware.InvokeAsync(context, checker);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(called);
        Assert.Equal(WcsManagementPermissionCodes.RuntimeOperate, checker.LastPermission);
    }

    private static DefaultHttpContext NewContext(string method, bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/tasks";
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (authenticated)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], "test"));
        return context;
    }

    private sealed class StubPermissionChecker(bool allowed) : IPermissionChecker
    {
        public string? LastPermission { get; private set; }

        public Task<bool> HasPermissionAsync(string permissionCode, CancellationToken cancellationToken = default)
        {
            LastPermission = permissionCode;
            return Task.FromResult(allowed);
        }

        public async Task EnsurePermissionAsync(string permissionCode, CancellationToken cancellationToken = default)
        {
            if (!await HasPermissionAsync(permissionCode, cancellationToken))
                throw new PermissionDeniedException(permissionCode);
        }

        public Task<UserPermissionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserPermissionSnapshot("tester", 0, []));
    }
}
