using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using Wcs.Core.TransportScheduling;
using Wcs.Infrastructure.Persistence;

namespace Wcs.Host.IndustrialSecurity;

/// <summary>
/// Canonical platform capabilities for the human WCS management plane. Runtime workers,
/// PLC polling and automatic dispatch do not depend on these permissions or on IAM.
/// </summary>
public static class WcsManagementPermissionCodes
{
    public const string RuntimeView = "wcs.runtime.view";
    public const string RuntimeOperate = "wcs.runtime.operate";
    public const string AdministrationView = "wcs.administration.view";
    public const string OperationManage = "wcs.operation.manage";
    public const string ConfigurationChange = "wcs.configuration.change";
    public const string TaskReassign = "wcs.task.reassign";
    public const string TrafficForceRelease = "wcs.traffic.force-release";
    public const string VehicleOverrideLowBattery = "wcs.vehicle.override-low-battery";
    public const string VehicleManualCommand = "wcs.vehicle.manual-command";
    public const string PlcWriteSignal = "wcs.plc.write-signal";
    public const string RecoveryResolveConflict = "wcs.recovery.resolve-conflict";
    public const string CommandRetryCompensation = "wcs.command.retry-compensation";
    public const string OperationApproveCritical = "wcs.operation.approve-critical";

    public static readonly IReadOnlyDictionary<string, string> CanonicalToTransport =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RuntimeView] = RuntimeView,
            [RuntimeOperate] = RuntimeOperate,
            [AdministrationView] = TransportPermissions.ReadAdministration,
            // Preserve the existing WCS.Task.Edit gate as the coarse operation-management
            // permission. The governance service still performs operation-specific checks.
            [OperationManage] = TransportPermissions.ChangeConfiguration,
            [ConfigurationChange] = TransportPermissions.ChangeConfiguration,
            [TaskReassign] = TransportPermissions.ReassignTask,
            [TrafficForceRelease] = TransportPermissions.ForceReleaseTraffic,
            [VehicleOverrideLowBattery] = TransportPermissions.OverrideLowBattery,
            [VehicleManualCommand] = TransportPermissions.SendManualDriverCommand,
            [PlcWriteSignal] = TransportPermissions.WritePlcSignal,
            [RecoveryResolveConflict] = TransportPermissions.ResolveRecoveryConflict,
            [CommandRetryCompensation] = TransportPermissions.RetryCommandCompensation,
            [OperationApproveCritical] = TransportPermissions.ApproveCriticalOperation
        };

    public static string ToTransportPermission(string permissionCode)
    {
        if (CanonicalToTransport.TryGetValue(permissionCode, out var transport))
            return transport;

        return permissionCode switch
        {
            "WCS.Task.View" => TransportPermissions.ReadAdministration,
            "WCS.Task.Edit" => TransportPermissions.ChangeConfiguration,
            "WCS.RGV.ForceRelease" => TransportPermissions.ForceReleaseTraffic,
            "WCS.RGV.Dispatch" => TransportPermissions.SendManualDriverCommand,
            _ => permissionCode
        };
    }

    public static string ToCanonicalPermission(string permissionCode)
    {
        foreach (var pair in CanonicalToTransport)
        {
            if (string.Equals(pair.Value, permissionCode, StringComparison.OrdinalIgnoreCase))
            {
                // ChangeConfiguration has two canonical aliases. ConfigurationChange is the
                // stable fine-grained capability exported from local permission snapshots.
                return string.Equals(permissionCode, TransportPermissions.ChangeConfiguration, StringComparison.OrdinalIgnoreCase)
                    ? ConfigurationChange
                    : pair.Key;
            }
        }

        return permissionCode switch
        {
            "WCS.Task.View" => AdministrationView,
            "WCS.Task.Edit" => ConfigurationChange,
            "WCS.RGV.ForceRelease" => TrafficForceRelease,
            "WCS.RGV.Dispatch" => VehicleManualCommand,
            _ => permissionCode
        };
    }
}

public sealed class WcsCurrentUser(IHttpContextAccessor httpContextAccessor, IConfiguration configuration) : ICurrentUser
{
    private ClaimsPrincipal Principal => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
    private bool CentralizedAuthentication => string.Equals(
        configuration["Security:Authentication:Mode"],
        "Centralized",
        StringComparison.OrdinalIgnoreCase);

    public IdentitySource Source
    {
        get
        {
            if (Enum.TryParse<IdentitySource>(Principal.FindFirstValue(IndustrialClaimTypes.IdentitySource), true, out var source))
                return source;

            return CentralizedAuthentication ? IdentitySource.Platform : IdentitySource.Local;
        }
    }

    public string? LocalUserId => Principal.FindFirstValue(IndustrialClaimTypes.LocalUserId)
        ?? (Source == IdentitySource.Local
            ? Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? Principal.Identity?.Name
            : null);

    public string? GlobalUserId => Principal.FindFirstValue(IndustrialClaimTypes.GlobalUserId)
        ?? (Source == IdentitySource.Platform
            ? Principal.FindFirstValue("sub") ?? Principal.FindFirstValue(ClaimTypes.NameIdentifier)
            : null);

    public string? UserId => LocalUserId ?? GlobalUserId;

    public string? UserName => Principal.FindFirstValue("preferred_username")
        ?? Principal.FindFirstValue("username")
        ?? Principal.FindFirstValue(ClaimTypes.Name)
        ?? Principal.FindFirstValue("name")
        ?? Principal.Identity?.Name
        ?? UserId;

    public string? TenantId => Principal.FindFirstValue(IndustrialClaimTypes.TenantId)
        ?? Principal.FindFirstValue(IndustrialClaimTypes.LegacyTenant);

    public IReadOnlyCollection<string> Roles => Principal.Claims
        .Where(c => c.Type == ClaimTypes.Role
            || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase))
        .Select(c => c.Value)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public long PermissionVersion => long.TryParse(
        Principal.FindFirstValue(IndustrialClaimTypes.PermissionVersion) ?? Principal.FindFirstValue("PermissionVersion"),
        out var version) ? version : 0;

    public bool IsAuthenticated
    {
        get
        {
            if (Principal.Identity?.IsAuthenticated != true) return false;
            return Source == IdentitySource.Platform
                ? !string.IsNullOrWhiteSpace(GlobalUserId)
                : !string.IsNullOrWhiteSpace(LocalUserId);
        }
    }
}

public sealed class WcsIdentityProvider(WcsCurrentUser currentUser) : IIdentityProvider
{
    public CurrentIdentity GetCurrentIdentity() => new(
        currentUser.UserId,
        currentUser.UserName,
        currentUser.TenantId,
        currentUser.Source,
        currentUser.GlobalUserId,
        currentUser.Roles,
        currentUser.PermissionVersion,
        currentUser.IsAuthenticated,
        currentUser.LocalUserId);
}

public sealed class WcsLocalPermissionSource(IHttpContextAccessor httpContextAccessor) : ILocalPermissionSource
{
    internal static readonly string[] AllPermissions =
    [
        WcsManagementPermissionCodes.RuntimeView,
        WcsManagementPermissionCodes.RuntimeOperate,
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

        var actualUserId = principal.FindFirstValue(IndustrialClaimTypes.LocalUserId)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(userId)
            && !string.IsNullOrWhiteSpace(actualUserId)
            && !string.Equals(userId, actualUserId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var transportCode = WcsManagementPermissionCodes.ToTransportPermission(permissionCode);
        if (principal.IsInRole("Administrator") || principal.IsInRole("WcsAdministrator"))
            return Task.FromResult(AllPermissions.Contains(transportCode, StringComparer.OrdinalIgnoreCase) || permissionCode == "*");

        var granted = principal.Claims
            .Where(c => string.Equals(c.Type, "permission", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Type, "permissions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return Task.FromResult(granted.Any(p => p == "*"
            || string.Equals(p, permissionCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p, transportCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WcsManagementPermissionCodes.ToCanonicalPermission(p), permissionCode, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class WcsPermissionCodeMapper : IPermissionCodeMapper
{
    public PermissionMappingResult Map(string permissionCode)
    {
        var normalized = permissionCode?.Trim() ?? string.Empty;
        if (normalized == "*") return new(normalized, true, new[] { "*" }, "WCS administrator wildcard");

        var local = WcsManagementPermissionCodes.ToTransportPermission(normalized);
        var known = WcsManagementPermissionCodes.CanonicalToTransport.ContainsKey(normalized)
            || WcsLocalPermissionSource.AllPermissions.Contains(local, StringComparer.OrdinalIgnoreCase)
            || normalized is "WCS.Task.View" or "WCS.Task.Edit" or "WCS.RGV.ForceRelease" or "WCS.RGV.Dispatch";

        return new(
            permissionCode ?? string.Empty,
            known,
            known && !string.IsNullOrWhiteSpace(local) ? new[] { local } : Array.Empty<string>(),
            known ? "Canonical WCS management capability -> TransportPermissions" : "Unknown WCS permission");
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
            var local = principal.Claims
                .Where(c => string.Equals(c.Type, "permission", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Type, "permissions", StringComparison.OrdinalIgnoreCase))
                .SelectMany(c => c.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var permission in local)
                permissions.Add(WcsManagementPermissionCodes.ToCanonicalPermission(permission));

            if (principal.IsInRole("Administrator") || principal.IsInRole("WcsAdministrator"))
            {
                foreach (var permission in WcsLocalPermissionSource.AllPermissions)
                    permissions.Add(WcsManagementPermissionCodes.ToCanonicalPermission(permission));
            }
        }

        return Task.FromResult(new UserPermissionSnapshot(userId, 0, permissions));
    }
}

/// <summary>Persists the stable IAM-to-WCS identity used by local audit records.</summary>
public sealed class WcsShadowUserResolver(ISqlSugarClient db) : IShadowUserResolver
{
    public async Task<ShadowUserSnapshot?> ResolveAsync(string iamUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(iamUserId)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var entity = await db.Queryable<WcsShadowUserEntity>()
            .Where(x => x.IamUserId == iamUserId)
            .FirstAsync();
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<ShadowUserSnapshot?> EnsureAsync(
        string iamUserId,
        string? userName,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(iamUserId)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var entity = await db.Queryable<WcsShadowUserEntity>()
            .Where(x => x.IamUserId == iamUserId)
            .FirstAsync();
        if (entity is not null)
        {
            entity.UserName = Normalize(userName, 100);
            entity.DisplayName = Normalize(displayName, 200);
            entity.Status = "Active";
            entity.UpdatedAt = DateTime.UtcNow;
            await db.Updateable(entity).ExecuteCommandAsync();
            return ToSnapshot(entity);
        }

        entity = new WcsShadowUserEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            IamUserId = iamUserId,
            LocalUserId = Normalize($"wcs:{iamUserId}", 100)!,
            UserName = Normalize(userName, 100),
            DisplayName = Normalize(displayName, 200),
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        try
        {
            await db.Insertable(entity).ExecuteCommandAsync();
            return ToSnapshot(entity);
        }
        catch
        {
            // A concurrent first request may have inserted the unique IAM mapping.
            var existing = await db.Queryable<WcsShadowUserEntity>()
                .Where(x => x.IamUserId == iamUserId)
                .FirstAsync();
            if (existing is null) throw;
            return ToSnapshot(existing);
        }
    }

    private static ShadowUserSnapshot ToSnapshot(WcsShadowUserEntity entity) => new(
        entity.Id,
        IndustrialSystemCodes.Wcs,
        entity.LocalUserId,
        entity.IamUserId,
        entity.UserName,
        entity.DisplayName,
        null,
        null,
        IdentitySource.Platform,
        entity.Status,
        entity.CreatedAt,
        entity.UpdatedAt);

    private static string? Normalize(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
