namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessRequirements
{
    public static IReadOnlyList<string> ExternalS9Prerequisites { get; } =
    [
        "real-plc-rgv-hardware",
        "approved-site-topology-and-point-map",
        "industrial-network-and-protocol-validation",
        "emergency-stop-and-mechanical-interlock-validation",
        "site-permissions-credentials-and-change-window",
        "operator-maintenance-and-rollback-signoff"
    ];
}
