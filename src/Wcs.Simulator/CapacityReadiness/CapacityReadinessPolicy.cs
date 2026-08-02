namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessPolicy
{
    public static bool IsSoftwareReadyForS9(HilReadinessSnapshot snapshot) =>
        snapshot.ReadyToEnterS9 &&
        snapshot.SimulationIsolationVerified &&
        snapshot.ProductionFailClosedVerified &&
        snapshot.NoProductionControlWritesVerified &&
        !snapshot.RealHilExecuted &&
        !snapshot.MechanicalSafetyAccepted &&
        !snapshot.SiteAccepted;
}
