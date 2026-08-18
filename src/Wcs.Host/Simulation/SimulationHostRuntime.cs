namespace Wcs.Host.Simulation;

using Wcs.Simulator.CapacityReadiness;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualIntegration;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

/// <summary>
/// One process-scoped composition root for the governed scenario catalog,
/// deterministic engine, S2-S8 virtual runtimes and bounded run registry.
/// It is inert until a Simulation-only controller operation is invoked.
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
        VirtualTrafficOptions virtualTrafficOptions,
        VirtualExternalOptions virtualExternalOptions,
        VirtualHealthOptions virtualHealthOptions,
        VirtualIntegrationOptions virtualIntegrationOptions,
        CapacityReadinessOptions capacityReadinessOptions)
    {
        Catalog = new SimulationScenarioCatalog();
        EngineOptions = engineOptions;
        RunOptions = runOptions;
        VirtualPlcOptions = virtualPlcOptions;
        VirtualRgvOptions = virtualRgvOptions;
        VirtualTrafficOptions = virtualTrafficOptions;
        VirtualExternalOptions = virtualExternalOptions;
        VirtualHealthOptions = virtualHealthOptions;
        VirtualIntegrationOptions = virtualIntegrationOptions;
        CapacityReadinessOptions = capacityReadinessOptions;

        var integrationActions = VirtualIntegrationScenarioHandlers.CreateActions(
            virtualIntegrationOptions,
            virtualPlcOptions,
            virtualRgvOptions,
            virtualTrafficOptions,
            virtualExternalOptions,
            virtualHealthOptions);
        var integrationAssertions = VirtualIntegrationScenarioHandlers.CreateAssertions(
            virtualIntegrationOptions,
            virtualPlcOptions,
            virtualRgvOptions,
            virtualTrafficOptions,
            virtualExternalOptions,
            virtualHealthOptions);

        var actions = VirtualPlcScenarioHandlers.CreateActions(virtualPlcOptions)
            .Concat(VirtualRgvScenarioHandlers.CreateActions(virtualRgvOptions))
            .Concat(VirtualTrafficScenarioHandlers.CreateActions(virtualTrafficOptions, virtualRgvOptions))
            .Concat(VirtualExternalScenarioHandlers.CreateActions(virtualExternalOptions))
            .Concat(VirtualHealthScenarioHandlers.CreateActions(virtualHealthOptions))
            .Concat(integrationActions)
            .ToArray();
        var assertions = VirtualPlcScenarioHandlers.CreateAssertions(virtualPlcOptions)
            .Concat(VirtualRgvScenarioHandlers.CreateAssertions(virtualRgvOptions))
            .Concat(VirtualTrafficScenarioHandlers.CreateAssertions(virtualTrafficOptions, virtualRgvOptions))
            .Concat(VirtualExternalScenarioHandlers.CreateAssertions(virtualExternalOptions))
            .Concat(VirtualHealthScenarioHandlers.CreateAssertions(virtualHealthOptions))
            .Concat(integrationAssertions)
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
    public VirtualExternalOptions VirtualExternalOptions { get; }
    public VirtualHealthOptions VirtualHealthOptions { get; }
    public VirtualIntegrationOptions VirtualIntegrationOptions { get; }
    public CapacityReadinessOptions CapacityReadinessOptions { get; }
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
            var virtualExternalOptions = configuration
                .GetSection(VirtualExternalOptions.SectionName)
                .Get<VirtualExternalOptions>() ?? new VirtualExternalOptions();
            var virtualHealthOptions = configuration
                .GetSection(VirtualHealthOptions.SectionName)
                .Get<VirtualHealthOptions>() ?? new VirtualHealthOptions();
            var virtualIntegrationOptions = configuration
                .GetSection(VirtualIntegrationOptions.SectionName)
                .Get<VirtualIntegrationOptions>() ?? new VirtualIntegrationOptions();
            var capacityReadinessOptions = configuration
                .GetSection(CapacityReadinessOptions.SectionName)
                .Get<CapacityReadinessOptions>() ?? new CapacityReadinessOptions();
            engineOptions.Validate();
            runOptions.Validate();
            virtualPlcOptions.Validate();
            virtualRgvOptions.Validate();
            virtualTrafficOptions.Validate();
            virtualExternalOptions.Validate();
            virtualHealthOptions.Validate();
            virtualIntegrationOptions.Validate();
            capacityReadinessOptions.Validate();
            _instance = new SimulationHostRuntime(
                engineOptions,
                runOptions,
                virtualPlcOptions,
                virtualRgvOptions,
                virtualTrafficOptions,
                virtualExternalOptions,
                virtualHealthOptions,
                virtualIntegrationOptions,
                capacityReadinessOptions);
            return _instance;
        }
    }
}
