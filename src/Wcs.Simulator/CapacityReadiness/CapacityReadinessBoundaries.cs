namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessBoundaries
{
    public static void ValidateProfileAgainstIntegration(
        CapacityProfileDefinition definition,
        CapacityReadinessOptions capacityOptions,
        VirtualIntegration.VirtualIntegrationOptions integrationOptions)
    {
        CapacityProfileValidation.Validate(definition, capacityOptions);
        ArgumentNullException.ThrowIfNull(integrationOptions);
        integrationOptions.Validate();

        if (definition.MissionCount > integrationOptions.MaximumMissions)
            throw new InvalidOperationException("Capacity profile mission count exceeds SimulationVirtualIntegration.MaximumMissions.");
        if (definition.SegmentsPerMission > integrationOptions.MaximumSegmentsPerMission)
            throw new InvalidOperationException("Capacity profile segments per mission exceed SimulationVirtualIntegration.MaximumSegmentsPerMission.");
    }
}
