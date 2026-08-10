namespace Wcs.Simulator.Verification;

/// <summary>
/// Time abstraction used by simulation-only infrastructure.
/// Production control semantics are intentionally untouched; simulator components
/// can opt into this clock to make long-running scenarios deterministic and fast.
/// </summary>
public interface ISimulationClock
{
    DateTime UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wall-clock implementation used by the simulator unless a manual clock is supplied.
/// </summary>
public sealed class SystemSimulationClock : ISimulationClock
{
    public static SystemSimulationClock Instance { get; } = new();

    private SystemSimulationClock()
    {
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Deterministic virtual clock. Delays complete only when time is advanced explicitly.
/// This allows minutes, hours, or days of simulator time to execute without sleeping
/// for the corresponding wall-clock duration.
/// </summary>
public sealed class ManualSimulationClock : ISimulationClock
{
    private sealed class DelayWaiter
    {
        public required DateTime DueUtc { get; init; }

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<DelayWaiter> _waiters = new();
    private DateTime _utcNow;

    public ManualSimulationClock(DateTime? initialUtc = null)
    {
        _utcNow = DateTime.SpecifyKind(
            initialUtc ?? DateTime.UnixEpoch,
            DateTimeKind.Utc);
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public int PendingDelayCount
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        DelayWaiter waiter;
        lock (_gate)
        {
            waiter = new DelayWaiter { DueUtc = _utcNow + delay };
            _waiters.Add(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var data = ((ManualSimulationClock Clock, DelayWaiter Waiter, CancellationToken Token))state!;
                    data.Clock.CancelWaiter(data.Waiter, data.Token);
                },
                (this, waiter, cancellationToken));
        }

        return waiter.Completion.Task;
    }

    public void AdvanceBy(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Simulation time cannot move backwards.");

        List<DelayWaiter> due;
        lock (_gate)
        {
            _utcNow += amount;
            due = _waiters.Where(x => x.DueUtc <= _utcNow).ToList();
            foreach (var waiter in due)
                _waiters.Remove(waiter);
        }

        CompleteWaiters(due);
    }

    public void AdvanceTo(DateTime utcTime)
    {
        var normalized = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        var current = UtcNow;
        if (normalized < current)
            throw new ArgumentOutOfRangeException(nameof(utcTime), "Simulation time cannot move backwards.");

        AdvanceBy(normalized - current);
    }

    private void CancelWaiter(DelayWaiter waiter, CancellationToken cancellationToken)
    {
        var removed = false;
        lock (_gate)
        {
            removed = _waiters.Remove(waiter);
        }

        if (!removed)
            return;

        // Do not Dispose the registration from inside its own callback; doing so can
        // synchronously wait for the callback that is currently executing.
        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    private static void CompleteWaiters(IEnumerable<DelayWaiter> waiters)
    {
        foreach (var waiter in waiters)
        {
            waiter.CancellationRegistration.Dispose();
            waiter.Completion.TrySetResult();
        }
    }
}
