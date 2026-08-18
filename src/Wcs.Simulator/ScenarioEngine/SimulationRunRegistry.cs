namespace Wcs.Simulator.ScenarioEngine;

using System.Collections.Concurrent;
using Wcs.Simulator.Governance;

public sealed class SimulationRunRegistryOptions
{
    public const string SectionName = "SimulationRunRegistry";

    public int MaximumRuns { get; set; } = 1_000;

    public void Validate()
    {
        if (MaximumRuns is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationRunRegistry.MaximumRuns must be between 1 and 100,000.");
    }
}

public sealed record SimulationRunSnapshot(
    Guid RunId,
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioManifestHash,
    SimulationSessionStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long CurrentOffsetMilliseconds,
    int NextTimelineIndex,
    int TimelineCount,
    int AssertionCount,
    string? FailureMessage,
    string? FinalStateHash,
    string? EvidenceHash);

/// <summary>
/// Bounded, process-memory-only run registry for governed deterministic scenarios.
/// It does not persist production SQL and does not connect to PLC, task, route,
/// reservation, vehicle or dispatch control paths.
/// </summary>
public sealed class SimulationRunRegistry
{
    private readonly ConcurrentDictionary<Guid, RunEntry> _runs = new();
    private readonly SimulationScenarioEngine _engine;
    private readonly SimulationRunRegistryOptions _options;
    private readonly object _createGate = new();

    public SimulationRunRegistry(
        SimulationScenarioEngine engine,
        SimulationRunRegistryOptions options)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public SimulationRunSnapshot Create(
        RegisteredSimulationScenario scenario,
        SimulationScenarioDefinition definition,
        double speedFactor = 1,
        bool startPaused = true)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(definition);

        var session = _engine.CreateSession(scenario, definition, speedFactor: speedFactor);
        if (startPaused)
            session.Pause();

        lock (_createGate)
        {
            if (_runs.Count >= _options.MaximumRuns)
                throw new InvalidOperationException("Simulation run registry has reached MaximumRuns.");

            var runId = Guid.NewGuid();
            var entry = new RunEntry(runId, scenario, session);
            if (!_runs.TryAdd(runId, entry))
                throw new InvalidOperationException("Simulation run registry could not atomically register the run.");
            return entry.ToSnapshot();
        }
    }

    public IReadOnlyCollection<SimulationRunSnapshot> List() =>
        _runs.Values
            .Select(static entry => entry.ToSnapshot())
            .OrderByDescending(static item => item.CreatedAtUtc)
            .ThenBy(static item => item.RunId)
            .ToArray();

    public bool TryGet(Guid runId, out SimulationRunSnapshot snapshot)
    {
        if (_runs.TryGetValue(runId, out var entry))
        {
            snapshot = entry.ToSnapshot();
            return true;
        }

        snapshot = default!;
        return false;
    }

    public async Task<SimulationRunSnapshot> StepAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var entry = GetRequired(runId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.EnsureRunnable();
            entry.StartedAtUtc ??= DateTimeOffset.UtcNow;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                entry.Cancellation.Token);
            try
            {
                await entry.Session.StepAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                entry.IsCancelled = true;
            }

            entry.CaptureTerminalState();
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<SimulationRunSnapshot> AdvanceAsync(
        Guid runId,
        long targetOffsetMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var entry = GetRequired(runId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.EnsureRunnable();
            entry.StartedAtUtc ??= DateTimeOffset.UtcNow;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                entry.Cancellation.Token);
            try
            {
                await entry.Session.RunUntilAsync(targetOffsetMilliseconds, linked.Token);
            }
            catch (OperationCanceledException)
            {
                entry.IsCancelled = true;
            }

            entry.CaptureTerminalState();
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<SimulationRunSnapshot> RunToCompletionAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var entry = GetRequired(runId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.EnsureRunnable();
            entry.StartedAtUtc ??= DateTimeOffset.UtcNow;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                entry.Cancellation.Token);
            try
            {
                entry.Result = await entry.Session.RunToCompletionAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                entry.IsCancelled = true;
            }

            entry.CaptureTerminalState();
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public SimulationRunSnapshot Pause(Guid runId)
    {
        var entry = GetRequired(runId);
        entry.Gate.Wait();
        try
        {
            entry.EnsureRunnable();
            entry.Session.Pause();
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public SimulationRunSnapshot Resume(Guid runId)
    {
        var entry = GetRequired(runId);
        entry.Gate.Wait();
        try
        {
            entry.EnsureRunnable();
            entry.StartedAtUtc ??= DateTimeOffset.UtcNow;
            entry.Session.Resume();
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public SimulationRunSnapshot SetSpeed(Guid runId, double speedFactor)
    {
        var entry = GetRequired(runId);
        entry.Gate.Wait();
        try
        {
            entry.EnsureRunnable();
            entry.Session.SetSpeed(speedFactor);
            return entry.ToSnapshot();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public SimulationCheckpoint CreateCheckpoint(Guid runId)
    {
        var entry = GetRequired(runId);
        entry.Gate.Wait();
        try
        {
            entry.EnsureRunnable();
            return entry.Session.CreateCheckpoint();
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public SimulationRunSnapshot Cancel(Guid runId)
    {
        var entry = GetRequired(runId);
        entry.Cancellation.Cancel();
        entry.IsCancelled = true;
        entry.FinishedAtUtc ??= DateTimeOffset.UtcNow;
        return entry.ToSnapshot();
    }

    private RunEntry GetRequired(Guid runId) =>
        _runs.TryGetValue(runId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Simulation run '{runId}' was not found.");

    private sealed class RunEntry
    {
        public RunEntry(
            Guid runId,
            RegisteredSimulationScenario scenario,
            SimulationExecutionSession session)
        {
            RunId = runId;
            Scenario = scenario;
            Session = session;
            CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        public Guid RunId { get; }
        public RegisteredSimulationScenario Scenario { get; }
        public SimulationExecutionSession Session { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset CreatedAtUtc { get; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public bool IsCancelled { get; set; }
        public SimulationRunResult? Result { get; set; }

        public void EnsureRunnable()
        {
            if (IsCancelled)
                throw new InvalidOperationException("The simulation run has been cancelled.");
            if (Session.Status is SimulationSessionStatus.Completed or SimulationSessionStatus.Failed or SimulationSessionStatus.Cancelled)
                throw new InvalidOperationException("The simulation run is already terminal.");
        }

        public void CaptureTerminalState()
        {
            if (IsCancelled || Session.Status is SimulationSessionStatus.Completed or SimulationSessionStatus.Failed or SimulationSessionStatus.Cancelled)
                FinishedAtUtc ??= DateTimeOffset.UtcNow;
        }

        public SimulationRunSnapshot ToSnapshot()
        {
            var status = IsCancelled ? SimulationSessionStatus.Cancelled : Session.Status;
            return new SimulationRunSnapshot(
                RunId,
                Scenario.ScenarioId,
                Scenario.Version,
                Scenario.ManifestHash,
                status,
                CreatedAtUtc,
                StartedAtUtc,
                FinishedAtUtc,
                Session.Clock.CurrentOffsetMilliseconds,
                Session.NextTimelineIndex,
                Session.TimelineCount,
                Session.AssertionOutcomes.Count,
                IsCancelled ? "Simulation execution was cancelled." : Session.FailureMessage,
                Result?.FinalStateHash,
                Result?.Evidence.EvidenceHash);
        }
    }
}
