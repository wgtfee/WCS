namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;

public sealed class SimulationGovernanceControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404EvenWhenBothSwitchesAreTrue()
    {
        var controller = CreateController("Production", simulatorEnabled: true, governanceEnabled: true);

        var result = controller.GetStatus();

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Status_InUnapprovedDevelopmentEnvironment_Returns404()
    {
        var controller = CreateController("Development", simulatorEnabled: true, governanceEnabled: true);

        var result = controller.GetStatus();

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Status_RequiresBothSwitches(bool simulatorEnabled, bool governanceEnabled)
    {
        var controller = CreateController("Simulation", simulatorEnabled, governanceEnabled);

        var result = controller.GetStatus();

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Status_InApprovedSimulationEnvironment_Returns200()
    {
        var controller = CreateController("Simulation", simulatorEnabled: true, governanceEnabled: true);

        var result = controller.GetStatus();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void ValidateScenario_RegistersValidPackageAndRejectsVersionMutation()
    {
        var controller = CreateController("Simulation", simulatorEnabled: true, governanceEnabled: true);
        var scenarioId = $"api-governance-{Guid.NewGuid():N}";
        var first = BuildRequest(scenarioId, "{\"actions\":[]}");
        var mutation = BuildRequest(scenarioId, "{\"actions\":[{\"type\":\"fault\"}]}");

        Assert.IsType<OkObjectResult>(controller.ValidateAndRegister(first));
        Assert.IsType<BadRequestObjectResult>(controller.ValidateAndRegister(mutation));
    }

    private static SimulationGovernanceController CreateController(
        string environmentName,
        bool simulatorEnabled,
        bool governanceEnabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["Simulator:Enabled"] = simulatorEnabled.ToString(),
            ["SimulationGovernance:Enabled"] = governanceEnabled.ToString(),
            ["SimulationGovernance:ScenarioDirectory"] = "data/simulation-scenarios",
            ["SimulationGovernance:MaximumScenarioBytes"] = "1048576",
            ["SimulationGovernance:MaximumEvidenceRecords"] = "10000",
            ["SimulationGovernance:AllowedEnvironments:0"] = "Simulation",
            ["SimulationGovernance:AllowedEnvironments:1"] = "SimulationLoadTest"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new SimulationGovernanceController(
            new TestHostEnvironment { EnvironmentName = environmentName },
            configuration);
    }

    private static ValidateSimulationScenarioRequest BuildRequest(string scenarioId, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260729,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "controller-test",
                ApprovedBy = "ci",
                ApprovedAtUtc = createdAt.AddMinutes(1)
            },
            ContentBase64 = Convert.ToBase64String(bytes)
        };
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Simulation";
        public string ApplicationName { get; set; } = "Wcs.Simulator.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
