namespace Wcs.Simulator.CapacityReadiness;

[Flags]
public enum CapacityReadinessFlags
{
    None = 0,
    CapacityBoundary = 1,
    EightHourVirtualSoak = 2,
    TwentyFourHourVirtualSoak = 4,
    Replay = 8,
    CheckpointRestore = 16,
    Conservation = 32,
    ProductionIsolation = 64,
    NoControlWrites = 128
}
