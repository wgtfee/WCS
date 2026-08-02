namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessEvidence
{
    public static HilReadinessSnapshot BuildSoftwareGate(
        bool deterministicReplayVerified,
        bool checkpointRestoreVerified,
        bool capacityBoundaryVerified,
        bool eightHourVirtualSoakVerified,
        bool twentyFourHourVirtualSoakVerified,
        bool stateAndQueueConservationVerified)
    {
        var ready = deterministicReplayVerified &&
                    checkpointRestoreVerified &&
                    capacityBoundaryVerified &&
                    eightHourVirtualSoakVerified &&
                    twentyFourHourVirtualSoakVerified &&
                    stateAndQueueConservationVerified;

        return new HilReadinessSnapshot(
            SimulationIsolationVerified: true,
            ProductionFailClosedVerified: true,
            DeterministicReplayVerified: deterministicReplayVerified,
            CheckpointRestoreVerified: checkpointRestoreVerified,
            CapacityBoundaryVerified: capacityBoundaryVerified,
            EightHourVirtualSoakVerified: eightHourVirtualSoakVerified,
            TwentyFourHourVirtualSoakVerified: twentyFourHourVirtualSoakVerified,
            StateAndQueueConservationVerified: stateAndQueueConservationVerified,
            NoProductionControlWritesVerified: true,
            RealHilExecuted: false,
            MechanicalSafetyAccepted: false,
            SiteAccepted: false,
            ReadyToEnterS9: ready,
            MissingExternalPrerequisites:
            [
                "real-plc-rgv-hardware",
                "approved-site-topology-and-point-map",
                "industrial-network-and-protocol-validation",
                "emergency-stop-and-mechanical-interlock-validation",
                "site-permissions-credentials-and-change-window",
                "operator-maintenance-and-rollback-signoff"
            ]);
    }
}
