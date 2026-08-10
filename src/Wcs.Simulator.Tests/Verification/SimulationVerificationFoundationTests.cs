namespace Wcs.Simulator.Tests.Verification;

using Wcs.Simulator.DeviceSimulator;
using Wcs.Simulator.PlcSimulator;
using Wcs.Simulator.Verification;

public sealed class SimulationVerificationFoundationTests
{
    [Fact]
    public async Task ManualClock_CompletesDelayOnlyAfterVirtualTimeAdvances()
    {
        var start = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var clock = new ManualSimulationClock(start);

        var delay = clock.DelayAsync(TimeSpan.FromHours(2));

        clock.AdvanceBy(TimeSpan.FromHours(1));
        Assert.False(delay.IsCompleted);
        Assert.Equal(start.AddHours(1), clock.UtcNow);

        clock.AdvanceBy(TimeSpan.FromHours(1));
        await delay;

        Assert.True(delay.IsCompletedSuccessfully);
        Assert.Equal(start.AddHours(2), clock.UtcNow);
        Assert.Equal(0, clock.PendingDelayCount);
    }

    [Fact]
    public async Task ManualClock_CancellationRemovesPendingDelay()
    {
        var clock = new ManualSimulationClock();
        using var cts = new CancellationTokenSource();

        var delay = clock.DelayAsync(TimeSpan.FromHours(1), cts.Token);
        Assert.Equal(1, clock.PendingDelayCount);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
        Assert.Equal(0, clock.PendingDelayCount);
    }

    [Fact]
    public async Task ScheduledFaultInjector_ReplaysDeviceFaultAndRecoveryDeterministically()
    {
        var clock = new ManualSimulationClock();
        var signalSource = new SimulatorSignalSource();
        var device = new TestDevice("RGV01", signalSource);
        var injector = new ScheduledFaultInjector(signalSource, new[] { device }, clock);

        var run = injector.RunAsync(new[]
        {
            new ScheduledSimulationFault(TimeSpan.Zero, SimulationFaultKind.DeviceFault, "RGV01"),
            new ScheduledSimulationFault(TimeSpan.FromMinutes(5), SimulationFaultKind.DeviceRecover, "RGV01")
        });

        Assert.True(device.IsFaulted);
        Assert.False(run.IsCompleted);

        clock.AdvanceBy(TimeSpan.FromMinutes(5));
        await run;

        Assert.False(device.IsFaulted);
    }

    [Fact]
    public async Task ScheduledFaultInjector_CanDisconnectReconnectAndInjectSignalBurst()
    {
        var clock = new ManualSimulationClock();
        var signalSource = new SimulatorSignalSource();
        var injector = new ScheduledFaultInjector(signalSource, clock: clock);

        var run = injector.RunAsync(new[]
        {
            new ScheduledSimulationFault(TimeSpan.Zero, SimulationFaultKind.PlcDisconnect),
            new ScheduledSimulationFault(TimeSpan.FromMinutes(1), SimulationFaultKind.PlcReconnect),
            new ScheduledSimulationFault(TimeSpan.FromMinutes(1), SimulationFaultKind.SignalBurst, SignalCount: 16)
        });

        Assert.False(signalSource.IsConnected);

        clock.AdvanceBy(TimeSpan.FromMinutes(1));
        await run;

        Assert.True(signalSource.IsConnected);
        var signals = await signalSource.ReadAsync();
        Assert.Equal(16, signals.Count);
    }

    [Fact]
    public async Task InvariantEngine_CollectsViolationsAndFailsTheGate()
    {
        var start = new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc);
        var clock = new ManualSimulationClock(start);
        var engine = new InvariantEngine(clock);

        engine.Register(new DelegateSimulationInvariant(
            "segment-single-owner",
            _ => ValueTask.FromResult(SimulationInvariantResult.Fail("SEG-01 has two owners"))));

        var exception = await Assert.ThrowsAsync<SimulationInvariantViolationException>(
            () => engine.AssertAsync());

        var violation = Assert.Single(exception.Violations);
        Assert.Equal("segment-single-owner", violation.InvariantName);
        Assert.Equal("SEG-01 has two owners", violation.Message);
        Assert.Equal(start, violation.OccurredAtUtc);
        Assert.False(engine.Passed);
    }

    [Fact]
    public async Task InvariantEngine_PassesWhenAllRegisteredPropertiesHold()
    {
        var engine = new InvariantEngine(new ManualSimulationClock());

        engine.Register(new DelegateSimulationInvariant(
            "completed-task-never-restarts",
            _ => ValueTask.FromResult(SimulationInvariantResult.Pass())));
        engine.Register(new DelegateSimulationInvariant(
            "no-motion-during-emergency-stop",
            _ => ValueTask.FromResult(SimulationInvariantResult.Pass())));

        await engine.AssertAsync();

        Assert.True(engine.Passed);
        Assert.Equal(2, engine.Count);
        Assert.Empty(engine.Violations);
    }

    private sealed class TestDevice : DeviceSimulatorBase
    {
        public TestDevice(string deviceId, ISignalSource signalSource)
            : base(deviceId, deviceId, signalSource)
        {
        }

        public override Task StartAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
