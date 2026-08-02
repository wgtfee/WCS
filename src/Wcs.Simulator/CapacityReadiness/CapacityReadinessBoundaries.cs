namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessBoundaries
{
    public static void ValidateProfileAgainstIntegration(
        CapacityProfileDefinition definition,
        CapacityReadinessOptions capacityOptions,
        VirtualIntegration.VirtualIntegrationOptions integrationOptions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(capacityOptions);
        ArgumentNullException.ThrowIfNull(integrationOptions);
        capacityOptions.Validate();
        integrationOptions.Validate();

        if (string.IsNullOrWhiteSpace(definition.ProfileId) || definition.ProfileId.Length > 128)
            throw new InvalidOperationException("Capacity profile ProfileId must be between 1 and 128 characters.");
        if (definition.MissionCount is < 1 || definition.MissionCount > capacityOptions.MaximumMissionsPerProfile)
            throw new InvalidOperationException("Capacity profile mission count exceeds SimulationCapacityReadiness.MaximumMissionsPerProfile.");
        if (definition.ConcurrentMissions is < 1 ||
            definition.ConcurrentMissions > capacityOptions.MaximumConcurrentMissions ||
            definition.ConcurrentMissions > definition.MissionCount)
            throw new InvalidOperationException("Capacity profile concurrent mission count exceeds the configured S8 boundary.");
        if (definition.SegmentsPerMission is < 1 || definition.SegmentsPerMission > capacityOptions.MaximumSegmentsPerMission)
            throw new InvalidOperationException("Capacity profile segments per mission exceed SimulationCapacityReadiness.MaximumSegmentsPerMission.");
        if (definition.VirtualDurationMilliseconds < 1)
            throw new InvalidOperationException("Capacity profile virtual duration must be positive.");
        if (definition.Kind == CapacityProfileKind.EightHourVirtualSoak &&
            definition.VirtualDurationMilliseconds != capacityOptions.EightHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("EightHourVirtualSoak must remain exactly eight virtual hours.");
        if (definition.Kind == CapacityProfileKind.TwentyFourHourVirtualSoak &&
            definition.VirtualDurationMilliseconds != capacityOptions.TwentyFourHourVirtualDurationMilliseconds)
            throw new InvalidOperationException("TwentyFourHourVirtualSoak must remain exactly twenty-four virtual hours.");

        if (definition.MissionCount > integrationOptions.MaximumMissions)
            throw new InvalidOperationException("Capacity profile mission count exceeds SimulationVirtualIntegration.MaximumMissions.");
        if (definition.SegmentsPerMission > integrationOptions.MaximumSegmentsPerMission)
            throw new InvalidOperationException("Capacity profile segments per mission exceed SimulationVirtualIntegration.MaximumSegmentsPerMission.");
    }
}
