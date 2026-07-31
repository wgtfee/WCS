namespace Wcs.Host.Simulation;

using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

/// <summary>
/// One process-scoped composition root for the governed scenario catalog,
/// deterministic engine, virtual PLC, virtual RGV, virtual traffic contracts
/// and bounded run registry. It is inert until a Simulation-only controller
/// operation is invoked.
/// </summary>
public sealed class SimulationHostRuntime
{
    private static readonly object Gate = new();
    private static SimulationHostRuntime? _instance;

    private SimulationHostRuntime(
        SimulationScenarioEngineOptions engineOptions,
        SimulationRunRegistryOptions runOptions,
        VirtualPlcOptions virtualPlcOptions,
        VirtualRgvOptions virtualRgvOptions,
        VirtualTrafficOptions virtualTrafficOptions)
    {
        Catalog = new SimulationScenarioCatalog();
        EngineOptions = engineOptions;
        RunOptions = runOptions;
        VirtualPlcOptions = virtualPlcOptions;
        VirtualRgvOptions = virtualRgvOptions;
        VirtualTrafficOptions = virtualTrafficOptions;

        var actions = VirtualPlcScenarioHandlers.CreateActions(virtualPlcOptions)
            .Concat(VirtualRgvScenarioHandlers.CreateActions(virtualRgvOptions))
            .Concat(VirtualTrafficScenarioHandlers.CreateActions(virtualTrafficOptions, virtualRgvOptions))
            .ToArray();
        var assertions = VirtualPlcScenarioHandlers.CreateAssertions(virtualPlcOptions)
            .Concat(VirtualRgvScenarioHandlers.CreateAssertions(virtualRgvOptions))
            .Concat(VirtualTrafficScenarioHandlers.CreateAssertions(virtualTrafficOptions, virtualRgvOptions))
            .ToArray();
        Engine = new SimulationScenarioEngine(actions, assertions, engineOptions);
        Runs = new SimulationRunRegistry(Engine, runOptions);
    }

    public SimulationScenarioCatalog Catalog { get; }
    public SimulationScenarioEngineOptions EngineOptions { get; }
    public SimulationRunRegistryOptions RunOptions { get; }
    public VirtualPlcOptions VirtualPlcOptions { get; }
    public VirtualRgvOptions VirtualRgvOptions { get; }
    public VirtualTrafficOptions VirtualTrafficOptions { get; }
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
            var virtualPlcOptions = configuration
                .GetSection(VirtualPlcOptions.SectionName)
                .Get<VirtualPlcOptions>() ?? new VirtualPlcOptions();
            var virtualRgvOptions = configuration
                .GetSection(VirtualRgvOptions.SectionName)
                .Get<VirtualRgvOptions>() ?? new VirtualRgvOptions();
            var virtualTrafficOptions = configuration
                .GetSection(VirtualTrafficOptions.SectionName)
                .Get<VirtualTrafficOptions>() ?? new VirtualTrafficOptions();
            engineOptions.Validate();
            runOptions.Validate();
            virtualPlcOptions.Validate();
            virtualRgvOptions.Validate();
            virtualTrafficOptions.Validate();
            _instance = new SimulationHostRuntime(
                engineOptions,
                runOptions,
                virtualPlcOptions,
                virtualRgvOptions,
                virtualTrafficOptions);
            return _instance;
        }
    }
}
