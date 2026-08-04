using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Http;
using Wcs.Core.TransportScheduling;
using SqlSugar;
using Wcs.Infrastructure.Persistence;

namespace Wcs.Host.IndustrialSecurity;

public sealed class WcsCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal Principal => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();

    public string? UserId => Principal.FindFirstValue("local_user_id")
        ?? Principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? Principal.FindFirstValue("sub")
        ?? Principal.Identity?.Name;
    public string? UserName => Principal.FindFirstValue(ClaimTypes.Name)
        ?? Principal.Identity?.Name
        ?? UserId;
    public string? LocalUserId => Principal.FindFirstValue("local_user_id") ?? (Source == IdentitySource.Local ? UserId : null);
    public string? TenantId => Principal.FindFirstValue("tenant_id") ?? Principal.FindFirstValue("tenant");
    public IdentitySource Source => Enum.TryParse<IdentitySource>(Principal.FindFirstValue("identity_source"), true, out var source) ? source : IdentitySource.Local;
    public string? GlobalUserId => Principal.FindFirstValue("global_user_id") ?? (Source == IdentitySource.Platform ? Principal.FindFirstValue("sub") : null);
    public IReadOnlyCollection<string> Roles => Principal.Claims
        .Where(c => c.Type == ClaimTypes.Role || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase))
        .Select(c => c.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public long PermissionVersion => long.TryParse(Principal.FindFirstValue("permission_version"), out var version) ? version : 0;
    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;
}

public sealed class WcsIdentityProvider(WcsCurrentUser currentUser) : IIdentityProvider
{
    public CurrentIdentity GetCurrentIdentity() => new(currentUser.UserId, currentUser.UserName, currentUser.TenantId, currentUser.Source, currentUser.GlobalUserId, currentUser.Roles, currentUser.PermissionVersion, currentUser.IsAuthenticated, currentUser.LocalUserId);
}

public sealed class WcsLocalPermissionSource(IHttpContextAccessor httpContextAccessor) : ILocalPermissionSource
{
    internal static readonly string[] AllPermissions =
    [
        TransportPermissions.ReadAdministration,
        TransportPermissions.ChangeConfiguration,
        TransportPermissions.ReassignTask,
        TransportPermissions.ForceReleaseTraffic,
        TransportPermissions.OverrideLowBattery,
        TransportPermissions.SendManualDriverCommand,
        TransportPermissions.WritePlcSignal,
        TransportPermissions.ResolveRecoveryConflict,
        TransportPermissions.RetryCommandCompensation,
        TransportPermissions.ApproveCriticalOperation
    ];

    public Task<bool> HasPermissionAsync(string userId, string permissionCode, CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return Task.FromResult(false);

        var actualUserId = principal.FindFirstValue("local_user_id")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(actualUserId) &&
            !string.Equals(userId, actualUserId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var legacyCode = NormalizePermission(permissionCode);
        if (principal.IsInRole("Administrator") || principal.IsInRole("WcsAdministrator"))
            return Task.FromResult(AllPermissions.Contains(legacyCode, StringComparer.OrdinalIgnoreCase) || permissionCode == "*");

        var granted = principal.Claims
            .Where(c => string.Equals(c.Type, "permission", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.Type, "permissions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));

        return Task.FromResult(granted.Any(p => p == "*" || string.Equals(p, permissionCode, StringComparison.OrdinalIgnoreCase) || string.Equals(p, legacyCode, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizePermission(string permissionCode) => permissionCode switch
    {
        "WCS.Task.View" => TransportPermissions.ReadAdministration,
        "WCS.Task.Edit" => TransportPermissions.ChangeConfiguration,
        "WCS.RGV.ForceRelease" => TransportPermissions.ForceReleaseTraffic,
        "WCS.RGV.Dispatch" => TransportPermissions.SendManualDriverCommand,
        _ => permissionCode
    };
}

public sealed class WcsPermissionCodeMapper : IPermissionCodeMapper
{
    public PermissionMappingResult Map(string permissionCode)
    {
        var normalized = permissionCode?.Trim() ?? string.Empty;
        var local = normalized switch
        {
            "WCS.Task.View" => TransportPermissions.ReadAdministration,
            "WCS.Task.Edit" => TransportPermissions.ChangeConfiguration,
            "WCS.RGV.ForceRelease" => TransportPermissions.ForceReleaseTraffic,
            "WCS.RGV.Dispatch" => TransportPermissions.SendManualDriverCommand,
            _ => normalized
        };
        var known = normalized == "*" || WcsLocalPermissionSource.AllPermissions.Contains(local, StringComparer.OrdinalIgnoreCase);
        return new(permissionCode ?? string.Empty, known, string.IsNullOrWhiteSpace(local) ? Array.Empty<string>() : new[] { local }, known ? "WCS Role/Menu/Button" : "Unknown permission");
    }
}

public sealed class WcsLocalPermissionProvider(IHttpContextAccessor httpContextAccessor) : IUserPermissionProvider
{
    public Task<UserPermissionSnapshot> GetPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (principal?.Identity?.IsAuthenticated == true)
        {
            permissions.UnionWith(principal.Claims
                .Where(c => string.Equals(c.Type, "permission", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Type, "permissions", StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)));

            if (principal.IsInRole("Administrator") || principal.IsInRole("WcsAdministrator"))
                permissions.UnionWith(WcsLocalPermissionSource.AllPermissions);
        }

        return Task.FromResult(new UserPermissionSnapshot(userId, 0, permissions));
    }
}

public sealed class WcsShadowUserResolver(ISqlSugarClient db) : IShadowUserResolver
{
    private const string SystemCode = "WCS";
    public Task<ShadowUserSnapshot?> ResolveAsync(string iamUserId, CancellationToken cancellationToken = default)
        => Task.FromResult(db.Queryable<WcsShadowUserEntity>().First(x => x.IamUserId == iamUserId) is { } row ? ToSnapshot(row) : null);

    public Task<ShadowUserSnapshot?> EnsureAsync(string iamUserId, string? userName, string? displayName, CancellationToken cancellationToken = default)
    {
        var row = db.Queryable<WcsShadowUserEntity>().First(x => x.IamUserId == iamUserId);
        if (row is null)
        {
            row = new WcsShadowUserEntity { IamUserId = iamUserId, LocalUserId = $"WCS:{iamUserId}", UserName = userName, DisplayName = displayName };
            db.Insertable(row).ExecuteCommand();
        }
        else
        {
            db.Updateable<WcsShadowUserEntity>().SetColumns(x => new WcsShadowUserEntity { UserName = userName, DisplayName = displayName, UpdatedAt = DateTime.UtcNow }).Where(x => x.Id == row.Id).ExecuteCommand();
        }
        return Task.FromResult<ShadowUserSnapshot?>(ToSnapshot(row));
    }

    private static ShadowUserSnapshot ToSnapshot(WcsShadowUserEntity row) => new(row.Id, SystemCode, row.LocalUserId, row.IamUserId, row.UserName, row.DisplayName, null, null, IdentitySource.Platform, row.Status, row.CreatedAt, row.UpdatedAt);
}
