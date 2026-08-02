namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessGate
{
    public static HilReadinessSnapshot Evaluate(
        bool deterministicReplayVerified,
        bool checkpointRestoreVerified,
        bool capacityBoundaryVerified,
        bool eightHourVirtualSoakVerified,
        bool twentyFourHourVirtualSoakVerified,
        bool stateAndQueueConservationVerified) =>
        CapacityReadinessEvidence.BuildSoftwareGate(
            deterministicReplayVerified,
            checkpointRestoreVerified,
            capacityBoundaryVerified,
            eightHourVirtualSoakVerified,
            twentyFourHourVirtualSoakVerified,
            stateAndQueueConservationVerified);
}
