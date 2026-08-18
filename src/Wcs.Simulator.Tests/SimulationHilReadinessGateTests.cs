namespace Wcs.Simulator.Tests;

using Wcs.Simulator.CapacityReadiness;

public sealed class SimulationHilReadinessGateTests
{
    [Fact]
    public void Contract_CannotRepresentRealHilAsExecutedByDefaultGate()
    {
        var gate = Snapshot(true);
        Assert.True(gate.ReadyToEnterS9);
        Assert.False(gate.RealHilExecuted);
        Assert.False(gate.MechanicalSafetyAccepted);
        Assert.False(gate.SiteAccepted);
    }

    [Fact]
    public void MissingProductionFailClosedEvidence_BlocksEntryToS9()
    {
        var gate = Snapshot(false);
        Assert.False(gate.ReadyToEnterS9);
        Assert.False(gate.ProductionFailClosedVerified);
    }

    [Fact]
    public void ExternalPrerequisites_RemainExplicitAfterSoftwareGatePasses()
    {
        var gate = Snapshot(true);
        Assert.Equal(3, gate.MissingExternalPrerequisites.Count);
        Assert.Contains(gate.MissingExternalPrerequisites, x => x.Contains("PLC", StringComparison.Ordinal));
        Assert.Contains(gate.MissingExternalPrerequisites, x => x.Contains("Mechanical", StringComparison.Ordinal));
        Assert.Contains(gate.MissingExternalPrerequisites, x => x.Contains("Site", StringComparison.Ordinal));
    }

    [Fact]
    public void CapacityOptions_KeepEightAndTwentyFourHourVirtualDurationsExact()
    {
        var options = new CapacityReadinessOptions();
        options.Validate();
        Assert.Equal(28_800_000, options.EightHourVirtualDurationMilliseconds);
        Assert.Equal(86_400_000, options.TwentyFourHourVirtualDurationMilliseconds);
    }

    [Fact]
    public void CapacityOptions_AreBounded()
    {
        Assert.Throws<InvalidOperationException>(() => new CapacityReadinessOptions { MaximumProfiles = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CapacityReadinessOptions { MaximumMissionsPerProfile = 100_001 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CapacityReadinessOptions { MaximumSamplesPerProfile = 1_000_001 }.Validate());
    }

    private static HilReadinessSnapshot Snapshot(bool productionFailClosed)
    {
        var ready = productionFailClosed;
        return new HilReadinessSnapshot(
            true, productionFailClosed, true, true, true, true, true, true, true,
            false, false, false, ready,
            ["Real PLC/RGV/MES/industrial network HIL execution", "Mechanical safety/interlock acceptance", "Site topology, credentials and trial-run acceptance"]);
    }
}
