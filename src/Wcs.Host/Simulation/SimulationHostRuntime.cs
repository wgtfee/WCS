namespace Wcs.Host.Simulation;

using Wcs.Simulator.ScenarioEngine;

/// <summary>
/// One process-scoped composition root for the governed scenario catalog,
/// deterministic engine and bounded run registry. It is inert until a
/// Simulation-only controller operation is invoked.
/// </summary>
public sealed class SimulationHostRuntime
{
    private static readonly object Gate = new();
    private static SimulationHostRuntime? _instance;

    private SimulationHostRuntime(
        SimulationScenarioEngineOptions engineOptions,
        SimulationRunRegistryOptions runOptions)
    {
        Catalog = new SimulationScenarioCatalog();
        EngineOptions = engineOptions;
        RunOptions = runOptions;
        Engine = new SimulationScenarioEngine(options: engineOptions);
        Runs = new SimulationRunRegistry(Engine, runOptions);
    }

    public SimulationScenarioCatalog Catalog { get; }
    public SimulationScenarioEngineOptions EngineOptions { get; }
    public SimulationRunRegistryOptions RunOptions { get; }
    public SimulationScenarioEngine Engine { get; }
    public SimulationRunRegistry Runs { get; }

    public static SimulationHostRuntime GetOrCreate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (Gate)
        {
            if (_instance is not null)
                return _instance;

            var engineOptions = configuration
                .GetSection("SimulationScenarioEngine")
                .Get<SimulationScenarioEngineOptions>() ?? new SimulationScenarioEngineOptions();
            var runOptions = configuration
                .GetSection(SimulationRunRegistryOptions.SectionName)
                .Get<SimulationRunRegistryOptions>() ?? new SimulationRunRegistryOptions();
            engineOptions.Validate();
            runOptions.Validate();
            _instance = new SimulationHostRuntime(engineOptions, runOptions);
            return _instance;
        }
    }
}
