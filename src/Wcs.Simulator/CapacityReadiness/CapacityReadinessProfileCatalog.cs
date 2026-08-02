namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessProfileCatalog
{
    public static CapacityProfileDefinition EightHour(
        string profileId,
        int missionCount,
        int concurrentMissions,
        int segmentsPerMission,
        long spacingMilliseconds = 0) =>
        new(profileId, CapacityProfileKind.EightHourVirtualSoak, missionCount, concurrentMissions,
            segmentsPerMission, CapacityReadinessConstants.EightHoursMilliseconds, spacingMilliseconds);

    public static CapacityProfileDefinition TwentyFourHour(
        string profileId,
        int missionCount,
        int concurrentMissions,
        int segmentsPerMission,
        long spacingMilliseconds = 0) =>
        new(profileId, CapacityProfileKind.TwentyFourHourVirtualSoak, missionCount, concurrentMissions,
            segmentsPerMission, CapacityReadinessConstants.TwentyFourHoursMilliseconds, spacingMilliseconds);
}
