using Wcs.Core.TransportScheduling;
using Wcs.Host.IndustrialSecurity;

namespace Wcs.Host.Tests.IndustrialSecurity;

public sealed class WcsManagementPermissionCodesTests
{
    [Theory]
    [InlineData(WcsManagementPermissionCodes.AdministrationView, TransportPermissions.ReadAdministration)]
    [InlineData(WcsManagementPermissionCodes.ConfigurationChange, TransportPermissions.ChangeConfiguration)]
    [InlineData(WcsManagementPermissionCodes.TaskReassign, TransportPermissions.ReassignTask)]
    [InlineData(WcsManagementPermissionCodes.TrafficForceRelease, TransportPermissions.ForceReleaseTraffic)]
    [InlineData(WcsManagementPermissionCodes.VehicleOverrideLowBattery, TransportPermissions.OverrideLowBattery)]
    [InlineData(WcsManagementPermissionCodes.VehicleManualCommand, TransportPermissions.SendManualDriverCommand)]
    [InlineData(WcsManagementPermissionCodes.PlcWriteSignal, TransportPermissions.WritePlcSignal)]
    [InlineData(WcsManagementPermissionCodes.RecoveryResolveConflict, TransportPermissions.ResolveRecoveryConflict)]
    [InlineData(WcsManagementPermissionCodes.CommandRetryCompensation, TransportPermissions.RetryCommandCompensation)]
    [InlineData(WcsManagementPermissionCodes.OperationApproveCritical, TransportPermissions.ApproveCriticalOperation)]
    public void CanonicalCapabilities_MapToExistingTransportPermissions(string canonical, string expected)
    {
        Assert.Equal(expected, WcsManagementPermissionCodes.ToTransportPermission(canonical));
    }

    [Theory]
    [InlineData("WCS.Task.View", TransportPermissions.ReadAdministration)]
    [InlineData("WCS.Task.Edit", TransportPermissions.ChangeConfiguration)]
    [InlineData("WCS.RGV.ForceRelease", TransportPermissions.ForceReleaseTraffic)]
    [InlineData("WCS.RGV.Dispatch", TransportPermissions.SendManualDriverCommand)]
    public void LegacyPhaseOneCodes_RemainLocallyCompatible(string legacy, string expected)
    {
        Assert.Equal(expected, WcsManagementPermissionCodes.ToTransportPermission(legacy));
    }

    [Fact]
    public void RuntimePermissionMap_ContainsOnlyHumanManagementCapabilities()
    {
        Assert.DoesNotContain(WcsManagementPermissionCodes.CanonicalToTransport.Keys,
            x => x.Contains("poll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(WcsManagementPermissionCodes.CanonicalToTransport.Keys,
            x => x.Contains("scheduler", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(WcsManagementPermissionCodes.CanonicalToTransport.Keys,
            x => x.Contains("orchestrator", StringComparison.OrdinalIgnoreCase));
    }
}
