namespace Wcs.Simulator.CapacityReadiness;

public static class CapacityReadinessInvariants
{
    public static bool ConservationSatisfied(CapacitySample sample) =>
        sample.DefinedMissions >= 0 &&
        sample.ActiveMissions >= 0 &&
        sample.AcknowledgedMissions >= 0 &&
        sample.ActiveReservations >= 0 &&
        sample.WaitingRequests >= 0 &&
        sample.ActiveDeadlocks >= 0 &&
        sample.ExternalRequests >= 0 &&
        sample.HealthOutcomes >= 0 &&
        sample.StateEntryCount >= 0 &&
        sample.ActiveMissions + sample.AcknowledgedMissions <= sample.DefinedMissions &&
        sample.ExternalRequests <= sample.DefinedMissions &&
        sample.HealthOutcomes <= sample.DefinedMissions;

    public static bool TerminalConservationSatisfied(CapacitySample sample) =>
        ConservationSatisfied(sample) &&
        sample.ActiveMissions == 0 &&
        sample.ActiveReservations == 0 &&
        sample.WaitingRequests == 0 &&
        sample.ActiveDeadlocks == 0 &&
        sample.AcknowledgedMissions == sample.DefinedMissions &&
        sample.ExternalRequests == sample.DefinedMissions &&
        sample.HealthOutcomes == sample.DefinedMissions;
}
