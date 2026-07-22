namespace Wcs.Host.Controllers;

using System.Security.Claims;
using Wcs.Core.TransportScheduling;

internal static class TransportOperatorIdentityFactory
{
    private static readonly string[] AllPermissions =
    {
        TransportPermissions.ReadAdministration,
        TransportPermissions.ChangeConfiguration,
        TransportPermissions.ReassignTask,
        TransportPermissions.ForceReleaseTraffic,
        TransportPermissions.OverrideLowBattery,
        TransportPermissions.SendManualDriverCommand,
        TransportPermissions.ApproveCriticalOperation
    };

    public static TransportOperatorIdentity Create(ClaimsPrincipal principal)
    {
        var authenticated = principal.Identity?.IsAuthenticated == true;
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity?.Name
            ?? string.Empty;
        var displayName = principal.FindFirst(ClaimTypes.Name)?.Value
            ?? principal.Identity?.Name
            ?? userId;

        var permissions = principal.Claims
            .Where(x => string.Equals(x.Type, "permission", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Type, "permissions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (principal.IsInRole("Administrator") || principal.IsInRole("WcsAdministrator"))
            permissions.UnionWith(AllPermissions);

        return new TransportOperatorIdentity
        {
            UserId = userId,
            DisplayName = displayName,
            IsAuthenticated = authenticated,
            Permissions = permissions
        };
    }
}
