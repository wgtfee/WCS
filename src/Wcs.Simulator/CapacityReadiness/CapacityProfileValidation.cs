namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityProfileValidation
{
    public static void Validate(CapacityProfileDefinition definition, CapacityReadinessOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (string.IsNullOrWhiteSpace(definition.ProfileId) || definition.ProfileId.Length > 128)
            throw new InvalidOperationException("Capacity profile id is required and must not exceed 128 characters.");
        if (definition.MissionCount is < 1 || definition.MissionCount > options.MaximumMissionsPerProfile)
            throw new InvalidOperationException("Capacity profile mission count is outside MaximumMissionsPerProfile.");
        if (definition.ConcurrentMissions is < 1 ||
            definition.ConcurrentMissions > options.MaximumConcurrentMissions ||
            definition.ConcurrentMissions > definition.MissionCount)
            throw new InvalidOperationException("Capacity profile concurrency is outside the configured boundary.");
        if (definition.SegmentsPerMission is < 1 || definition.SegmentsPerMission > options.MaximumSegmentsPerMission)
            throw new InvalidOperationException("Capacity profile segments per mission are outside MaximumSegmentsPerMission.");
        if (definition.VirtualDurationMilliseconds < 1 || definition.VirtualDurationMilliseconds > options.TwentyFourHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("Capacity profile virtual duration must be within the 24-hour virtual envelope.");
        if (definition.MissionSpacingMilliseconds < 0 || definition.MissionSpacingMilliseconds > definition.VirtualDurationMilliseconds)
            throw new InvalidOperationException("Capacity profile mission spacing must be within the profile duration.");
        if (definition.Kind == CapacityProfileKind.EightHourVirtualSoak &&
            definition.VirtualDurationMilliseconds != options.EightHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("EightHourVirtualSoak must use exactly 8 virtual hours.");
        if (definition.Kind == CapacityProfileKind.TwentyFourHourVirtualSoak &&
            definition.VirtualDurationMilliseconds != options.TwentyFourHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("TwentyFourHourVirtualSoak must use exactly 24 virtual hours.");
    }
}
