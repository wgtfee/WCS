namespace Wcs.Simulator.Verification;

using System.Collections.Concurrent;

/// <summary>
/// Result returned by a simulation invariant evaluation.
/// </summary>
public sealed record SimulationInvariantResult(bool Passed, string? Message = null)
{
    public static SimulationInvariantResult Pass() => new(true);

    public static SimulationInvariantResult Fail(string message) => new(false, message);
}

/// <summary>
/// A safety or correctness property that must remain true while a scenario runs.
/// Examples include single-owner route sections, no motion under emergency stop,
/// and no transition from Completed back to Running.
/// </summary>
public interface ISimulationInvariant
{
    string Name { get; }

    ValueTask<SimulationInvariantResult> EvaluateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Convenience invariant backed by a delegate.
/// </summary>
public sealed class DelegateSimulationInvariant : ISimulationInvariant
{
    private readonly Func<CancellationToken, ValueTask<SimulationInvariantResult>> _evaluate;

    public DelegateSimulationInvariant(
        string name,
        Func<CancellationToken, ValueTask<SimulationInvariantResult>> evaluate)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Invariant name is required.", nameof(name))
            : name;
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    }

    public string Name { get; }

    public ValueTask<SimulationInvariantResult> EvaluateAsync(CancellationToken cancellationToken = default)
        => _evaluate(cancellationToken);
}

public sealed record InvariantViolation(
    string InvariantName,
    string Message,
    DateTime OccurredAtUtc);

/// <summary>
/// Evaluates registered invariants and accumulates every violation instead of only
/// reporting the first failure. This is suitable for CI evidence and stress/soak runs.
/// </summary>
public sealed class InvariantEngine
{
    private readonly object _gate = new();
    private readonly List<ISimulationInvariant> _invariants = new();
    private readonly ConcurrentQueue<InvariantViolation> _violations = new();
    private readonly ISimulationClock _clock;

    public InvariantEngine(ISimulationClock? clock = null)
    {
        _clock = clock ?? SystemSimulationClock.Instance;
    }

    public IReadOnlyList<InvariantViolation> Violations => _violations.ToArray();

    public bool Passed => _violations.IsEmpty;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _invariants.Count;
            }
        }
    }

    public void Register(ISimulationInvariant invariant)
    {
        ArgumentNullException.ThrowIfNull(invariant);

        lock (_gate)
        {
            if (_invariants.Any(x => string.Equals(x.Name, invariant.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Invariant '{invariant.Name}' is already registered.");

            _invariants.Add(invariant);
        }
    }

    public async Task<IReadOnlyList<InvariantViolation>> EvaluateAllAsync(
        CancellationToken cancellationToken = default)
    {
        ISimulationInvariant[] snapshot;
        lock (_gate)
        {
            snapshot = _invariants.ToArray();
        }

        var newViolations = new List<InvariantViolation>();
        foreach (var invariant in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await invariant.EvaluateAsync(cancellationToken);
            if (result.Passed)
                continue;

            var violation = new InvariantViolation(
                invariant.Name,
                result.Message ?? "Invariant failed without a message.",
                _clock.UtcNow);

            _violations.Enqueue(violation);
            newViolations.Add(violation);
        }

        return newViolations;
    }

    public async Task AssertAsync(CancellationToken cancellationToken = default)
    {
        var violations = await EvaluateAllAsync(cancellationToken);
        if (violations.Count == 0)
            return;

        throw new SimulationInvariantViolationException(violations);
    }

    public void ResetViolations()
    {
        while (_violations.TryDequeue(out _))
        {
        }
    }
}

public sealed class SimulationInvariantViolationException : Exception
{
    public SimulationInvariantViolationException(IReadOnlyList<InvariantViolation> violations)
        : base(CreateMessage(violations))
    {
        Violations = violations;
    }

    public IReadOnlyList<InvariantViolation> Violations { get; }

    private static string CreateMessage(IReadOnlyList<InvariantViolation> violations)
        => $"Simulation invariant gate failed: {string.Join("; ", violations.Select(v => $"{v.InvariantName}: {v.Message}"))}";
}
