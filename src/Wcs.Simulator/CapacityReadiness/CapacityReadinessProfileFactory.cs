namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessProfileFactory
{
    public static CapacityProfileDefinition Nominal(string profileId) =>
        new(profileId, CapacityProfileKind.Nominal, 64, 16, 1, 3_600_000, 1_000);

    public static CapacityProfileDefinition Peak(string profileId) =>
        new(profileId, CapacityProfileKind.Peak, 128, 32, 1, 3_600_000, 500);

    public static CapacityProfileDefinition Saturation(string profileId) =>
        new(profileId, CapacityProfileKind.Saturation, 256, 64, 1, 3_600_000, 0);
}
