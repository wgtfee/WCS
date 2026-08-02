namespace Wcs.Simulator.CapacityReadiness;

public enum CapacityReadinessCheckKind
{
    CapacityBoundary = 0,
    EightHourVirtualSoak = 1,
    TwentyFourHourVirtualSoak = 2,
    DeterministicReplay = 3,
    CheckpointRestore = 4,
    StateAndQueueConservation = 5,
    ProductionIsolation = 6,
    NoControlWrites = 7
}
