namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;

public sealed class SimulationScenarioControllerTests
{
    [Fact]
    public void Runs_InProduction_Returns404()
    {
        var controller = new SimulationScenarioController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListRuns());
    }

    [Fact]
    public async Task GovernedScenario_CanStepCheckpointRunAndReplay()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var controller = new SimulationScenarioController(environment, configuration);
        var scenarioId = $"host-s1-{Guid.NewGuid():N}";
        var content = BuildScenarioJson(scenarioId);

        Assert.IsType<OkObjectResult>(governance.ValidateAndRegister(
            BuildGovernanceRequest(scenarioId, content)));

        var create = Assert.IsType<OkObjectResult>(controller.CreateRun(new CreateSimulationRunRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            SpeedFactor = 100,
            StartPaused = true
        }));
        var created = Assert.IsType<SimulationRunSnapshot>(create.Value);
        Assert.Equal(SimulationSessionStatus.Paused, created.Status);

        var step = Assert.IsType<OkObjectResult>(await controller.Step(created.RunId, CancellationToken.None));
        var stepped = Assert.IsType<SimulationRunSnapshot>(step.Value);
        Assert.Equal(1, stepped.NextTimelineIndex);
        Assert.Equal(SimulationSessionStatus.Paused, stepped.Status);

        Assert.IsType<OkObjectResult>(controller.CreateCheckpoint(created.RunId));
        Assert.IsType<OkObjectResult>(controller.Resume(created.RunId));

        var run = Assert.IsType<OkObjectResult>(await controller.RunToCompletion(created.RunId, CancellationToken.None));
        var completed = Assert.IsType<SimulationRunSnapshot>(run.Value);
        Assert.Equal(SimulationSessionStatus.Completed, completed.Status);
        Assert.NotNull(completed.FinalStateHash);
        Assert.NotNull(completed.EvidenceHash);

        var replay = Assert.IsType<OkObjectResult>(await controller.Replay(new ReplaySimulationScenarioRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
        }, CancellationToken.None));
        var comparison = Assert.IsType<SimulationReplayComparison>(replay.Value);
        Assert.True(comparison.Equivalent);
    }

    [Fact]
    public void CreateRun_RejectsContentDifferentFromGovernedHash()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var controller = new SimulationScenarioController(environment, configuration);
        var scenarioId = $"host-hash-{Guid.NewGuid():N}";
        var content = BuildScenarioJson(scenarioId);

        Assert.IsType<OkObjectResult>(governance.ValidateAndRegister(
            BuildGovernanceRequest(scenarioId, content)));

        var result = controller.CreateRun(new CreateSimulationRunRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content + " "))
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Cancel_CreatesTerminalCancelledSnapshot()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var controller = new SimulationScenarioController(environment, configuration);
        var scenarioId = $"host-cancel-{Guid.NewGuid():N}";
        var content = BuildScenarioJson(scenarioId);
        governance.ValidateAndRegister(BuildGovernanceRequest(scenarioId, content));

        var create = Assert.IsType<OkObjectResult>(controller.CreateRun(new CreateSimulationRunRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content))
        }));
        var created = Assert.IsType<SimulationRunSnapshot>(create.Value);

        var cancel = Assert.IsType<OkObjectResult>(controller.Cancel(created.RunId));
        var cancelled = Assert.IsType<SimulationRunSnapshot>(cancel.Value);
        Assert.Equal(SimulationSessionStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.FinishedAtUtc);
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
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
                Source = "host-controller-test",
                ApprovedBy = "ci",
                ApprovedAtUtc = createdAt.AddMinutes(1)
            },
            ContentBase64 = Convert.ToBase64String(bytes)
        };
    }

    private static string BuildScenarioJson(string scenarioId) => $$"""
    {
      "SchemaVersion": 1,
      "ScenarioId": "{{scenarioId}}",
      "Version": "1.0.0",
      "Seed": 20260729,
      "StartTimeUtc": "2026-07-29T00:00:00+00:00",
      "DurationMilliseconds": 1000,
      "StopOnAssertionFailure": true,
      "Actions": [
        {
          "Id": "set-counter",
          "AtMilliseconds": 100,
          "Order": 0,
          "Kind": "state.set",
          "Target": "counter",
          "Payload": 1
        },
        {
          "Id": "increment-counter",
          "AtMilliseconds": 200,
          "Order": 0,
          "Kind": "state.increment",
          "Target": "counter",
          "Payload": 2
        }
      ],
      "Assertions": [
        {
          "Id": "counter-equals-three",
          "AtMilliseconds": 300,
          "Order": 0,
          "Kind": "state.equals",
          "Target": "counter",
          "Expected": 3
        }
      ]
    }
    """;

    private static IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Simulator:Enabled"] = "true",
            ["SimulationGovernance:Enabled"] = "true",
            ["SimulationGovernance:ScenarioDirectory"] = "data/simulation-scenarios",
            ["SimulationGovernance:MaximumScenarioBytes"] = "1048576",
            ["SimulationGovernance:MaximumRegisteredScenarioVersions"] = "10000",
            ["SimulationGovernance:MaximumEvidenceRecords"] = "10000",
            ["SimulationGovernance:MaximumEvidenceValueCharacters"] = "4096",
            ["SimulationScenarioEngine:MaximumTimelineItems"] = "100000",
            ["SimulationScenarioEngine:MaximumStateEntries"] = "10000",
            ["SimulationScenarioEngine:MaximumStateValueCharacters"] = "4096",
            ["SimulationScenarioEngine:MaximumCheckpointBytes"] = "16777216",
            ["SimulationScenarioEngine:MaximumSpeedFactor"] = "1000",
            ["SimulationRunRegistry:MaximumRuns"] = "1000"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Simulation";
        public string ApplicationName { get; set; } = "Wcs.Simulator.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
