namespace Wcs.Simulator.Tests;

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;

public sealed class SimulationUnifiedConsoleTests
{
    [Fact]
    public void Controller_ProductionAlwaysReturns404()
    {
        var controller = Controller("Production", simulationEnabled: true, hilEnabled: true);
        Assert.IsType<NotFoundResult>(controller.GetOverview());
    }

    [Fact]
    public void Controller_SimulationEnvironmentReturnsOverview()
    {
        var result = Controller("Simulation", simulationEnabled: true, hilEnabled: false).GetOverview();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void Controller_HilEnvironmentReturnsOverview()
    {
        var result = Controller("HIL", simulationEnabled: false, hilEnabled: true).GetOverview();
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void Controller_UnapprovedEnvironmentReturns404()
    {
        var controller = Controller("Development", simulationEnabled: true, hilEnabled: true);
        Assert.IsType<NotFoundResult>(controller.GetOverview());
    }

    [Fact]
    public void Controller_ExposesOnlyOneHttpGetAction()
    {
        var methods = typeof(SimulationVerificationOverviewController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var method = Assert.Single(methods);
        Assert.NotEmpty(method.GetCustomAttributes<HttpGetAttribute>(inherit: true));
        Assert.DoesNotContain(method.GetCustomAttributes(inherit: true), attribute =>
            attribute is HttpPostAttribute or HttpPutAttribute or HttpPatchAttribute or HttpDeleteAttribute);
    }

    [Fact]
    public void Overview_ContainsOrderedS0ThroughS10Stages()
    {
        var overview = Overview("Simulation", simulationEnabled: true, hilEnabled: false);
        Assert.Equal(11, overview.Stages.Count);
        Assert.Equal(Enumerable.Range(0, 11).Select(x => $"S{x}"), overview.Stages.Select(x => x.Id));
    }

    [Fact]
    public void SimulationEnvironment_EnablesSimulationStagesButNotRealHilInspection()
    {
        var overview = Overview("Simulation", simulationEnabled: true, hilEnabled: false);
        Assert.True(overview.SimulationInspectionAvailable);
        Assert.False(overview.HilInspectionAvailable);
        Assert.Equal("Available", overview.Stages.Single(x => x.Id == "S0").Availability);
        Assert.Equal("UnavailableInCurrentEnvironment", overview.Stages.Single(x => x.Id == "S9").Availability);
        Assert.Equal("Available", overview.Stages.Single(x => x.Id == "S10").Availability);
    }

    [Fact]
    public void HilEnvironment_EnablesHilInspectionWithoutClaimingSimulationRuntime()
    {
        var overview = Overview("HIL", simulationEnabled: false, hilEnabled: true);
        Assert.False(overview.SimulationInspectionAvailable);
        Assert.True(overview.HilInspectionAvailable);
        Assert.Equal("UnavailableInCurrentEnvironment", overview.Stages.Single(x => x.Id == "S0").Availability);
        Assert.Equal("Available", overview.Stages.Single(x => x.Id == "S9").Availability);
        Assert.Equal("Available", overview.Stages.Single(x => x.Id == "S10").Availability);
    }

    [Fact]
    public void Overview_IsReadOnlyAndNeverClaimsRealAcceptance()
    {
        var overview = Overview("HIL", simulationEnabled: false, hilEnabled: true);
        Assert.True(overview.ReadOnly);
        Assert.False(overview.RemoteControlAllowed);
        Assert.False(overview.RealHilExecuted);
        Assert.False(overview.ProtocolValidated);
        Assert.False(overview.MechanicalSafetyAccepted);
        Assert.False(overview.SiteAccepted);
        Assert.True(overview.RealHilEvidenceRequiredForCompletion);
    }

    [Fact]
    public void EveryStage_IsReadOnlyAndS9RequiresRealHardware()
    {
        var overview = Overview("HIL", simulationEnabled: false, hilEnabled: true);
        Assert.All(overview.Stages, stage =>
        {
            Assert.True(stage.ReadOnlyInspection);
            Assert.StartsWith("/api/", stage.ApiPrefix, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(stage.SafetyBoundary));
        });
        Assert.True(overview.Stages.Single(x => x.Id == "S9").RequiresRealHardware);
        Assert.DoesNotContain(overview.Stages.Where(x => x.Id != "S9"), x => x.RequiresRealHardware);
    }

    private static SimulationVerificationOverview Overview(
        string environment,
        bool simulationEnabled,
        bool hilEnabled)
    {
        var result = Assert.IsType<OkObjectResult>(
            Controller(environment, simulationEnabled, hilEnabled).GetOverview());
        return Assert.IsType<SimulationVerificationOverview>(result.Value);
    }

    private static SimulationVerificationOverviewController Controller(
        string environment,
        bool simulationEnabled,
        bool hilEnabled) =>
        new(new TestEnvironment(environment), Configuration(simulationEnabled, hilEnabled));

    private static IConfiguration Configuration(bool simulationEnabled, bool hilEnabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["Simulator:Enabled"] = simulationEnabled ? "true" : "false",
            ["SimulationGovernance:Enabled"] = simulationEnabled ? "true" : "false",
            ["SimulationGovernance:AllowedEnvironments:0"] = "Simulation",
            ["SimulationGovernance:AllowedEnvironments:1"] = "SimulationLoadTest",
            ["HilVerification:Enabled"] = hilEnabled ? "true" : "false",
            ["HilVerification:RequireDualApproval"] = "true",
            ["HilVerification:RequireSelfHostedHilRunner"] = "true",
            ["HilVerification:AllowedEnvironments:0"] = "HIL",
            ["HilVerification:AllowedEnvironments:1"] = "TrialRun"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Wcs.Host.Tests";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
