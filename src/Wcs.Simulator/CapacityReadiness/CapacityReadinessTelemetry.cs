namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessTelemetry
{
    public static CapacitySample Terminal(
        long sequence,
        long virtualOffsetMilliseconds,
        int missions,
        long stateEntryCount) =>
        new(sequence, virtualOffsetMilliseconds, missions, 0, missions, 0, 0, 0, missions, missions, stateEntryCount);
}
