namespace Wcs.Host.Simulation;

using Wcs.Simulator.Governance;

/// <summary>
/// Host-scoped shared catalog for governed simulation scenario versions.
/// The catalog remains process-memory only and delegates all immutability,
/// capacity and SHA validation to the S0 governance registry.
/// </summary>
public sealed class SimulationScenarioCatalog
{
    private readonly SimulationScenarioRegistry _registry = new();

    public RegisteredSimulationScenario Register(
        SimulationScenarioPackage package,
        SimulationGovernanceOptions options,
        DateTimeOffset? registeredAtUtc = null) =>
        _registry.Register(package, options, registeredAtUtc);

    public IReadOnlyCollection<RegisteredSimulationScenario> List() =>
        _registry.List();

    public bool TryGet(
        string scenarioId,
        string version,
        out RegisteredSimulationScenario scenario)
    {
        scenario = _registry.List().FirstOrDefault(item =>
            string.Equals(item.ScenarioId, scenarioId, StringComparison.Ordinal) &&
            string.Equals(item.Version, version, StringComparison.Ordinal))!;
        return scenario is not null;
    }
}
