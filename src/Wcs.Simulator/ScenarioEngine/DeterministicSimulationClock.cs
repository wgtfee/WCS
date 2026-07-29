namespace Wcs.Simulator.ScenarioEngine;

public sealed class DeterministicSimulationClock
{
    private readonly long _durationMilliseconds;

    public DeterministicSimulationClock(
        DateTimeOffset startTimeUtc,
        long durationMilliseconds,
        double speedFactor = 1)
    {
        if (startTimeUtc == default)
            throw new ArgumentException("Simulation clock start time is required.", nameof(startTimeUtc));
        if (durationMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));

        StartTimeUtc = startTimeUtc.ToUniversalTime();
        _durationMilliseconds = durationMilliseconds;
        SetSpeed(speedFactor, 100_000);
    }

    public DateTimeOffset StartTimeUtc { get; }
    public long CurrentOffsetMilliseconds { get; private set; }
    public long DurationMilliseconds => _durationMilliseconds;
    public DateTimeOffset CurrentTimeUtc => StartTimeUtc.AddMilliseconds(CurrentOffsetMilliseconds);
    public double SpeedFactor { get; private set; }

    public void SetSpeed(double speedFactor, double maximumSpeedFactor)
    {
        if (!double.IsFinite(speedFactor) || speedFactor <= 0 || speedFactor > maximumSpeedFactor)
            throw new ArgumentOutOfRangeException(nameof(speedFactor),
                $"Speed factor must be greater than zero and no more than {maximumSpeedFactor}.");
        SpeedFactor = speedFactor;
    }

    public void AdvanceBy(long milliseconds) =>
        AdvanceTo(checked(CurrentOffsetMilliseconds + milliseconds));

    public void AdvanceTo(long offsetMilliseconds)
    {
        if (offsetMilliseconds < CurrentOffsetMilliseconds)
            throw new InvalidOperationException("Simulation clock cannot move backwards.");
        if (offsetMilliseconds > _durationMilliseconds)
            throw new InvalidOperationException("Simulation clock cannot advance beyond scenario duration.");
        CurrentOffsetMilliseconds = offsetMilliseconds;
    }

    public void Restore(long offsetMilliseconds)
    {
        if (offsetMilliseconds < 0 || offsetMilliseconds > _durationMilliseconds)
            throw new InvalidOperationException("Checkpoint clock offset is outside scenario duration.");
        CurrentOffsetMilliseconds = offsetMilliseconds;
    }
}
