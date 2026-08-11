namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualPlc;

public sealed class SimulationVirtualPlcControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualPlcController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveRun_CanInspectBlockFaultStatusAndAudit()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var virtualPlc = new SimulationVirtualPlcController(environment, configuration);
        var scenarioId = $"host-s2-{Guid.NewGuid():N}";
        var content = BuildScenarioJson(scenarioId);

        Assert.IsType<OkObjectResult>(governance.ValidateAndRegister(
            BuildGovernanceRequest(scenarioId, content)));

        var create = Assert.IsType<OkObjectResult>(scenarios.CreateRun(new CreateSimulationRunRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            StartPaused = true
        }));
        var run = Assert.IsType<SimulationRunSnapshot>(create.Value);

        Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));

        var statusResult = Assert.IsType<OkObjectResult>(virtualPlc.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualPlcStatusSnapshot>(statusResult.Value);
        Assert.Equal(1, status.BlockCount);
        Assert.Equal(1, status.FaultCount);
        Assert.Equal(1, status.ActiveFaultCount);

        var blocksResult = Assert.IsType<OkObjectResult>(virtualPlc.ListBlocks(run.RunId));
        var blocks = Assert.IsAssignableFrom<IReadOnlyList<string>>(blocksResult.Value);
        Assert.Contains("PLC1.DB1", blocks);

        var blockResult = Assert.IsType<OkObjectResult>(virtualPlc.GetBlock(run.RunId, "PLC1", 1));
        var block = Assert.IsType<VirtualPlcBlockSnapshot>(blockResult.Value);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, block.Data);

        var faultsResult = Assert.IsType<OkObjectResult>(virtualPlc.ListFaults(run.RunId));
        var faults = Assert.IsAssignableFrom<IReadOnlyList<VirtualPlcFaultSnapshot>>(faultsResult.Value);
        Assert.Single(faults);
        Assert.True(faults[0].Active);

        var auditResult = Assert.IsType<OkObjectResult>(virtualPlc.ListAudit(run.RunId, 10));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualPlcAuditRecord>>(auditResult.Value);
        Assert.Equal(2, audit.Count);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualPlcController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListBlocks(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
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
                Source = "host-s2-controller-test",
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
      "StartTimeUtc": "2026-07-29T12:00:00+00:00",
      "DurationMilliseconds": 1000,
      "StopOnAssertionFailure": true,
      "Actions": [
        {
          "Id": "define-db",
          "AtMilliseconds": 0,
          "Order": 0,
          "Kind": "plc.block.define",
          "Target": "PLC1.DB1",
          "Payload": { "Size": 4, "InitialBase64": "AQIDBA==" }
        },
        {
          "Id": "apply-fault",
          "AtMilliseconds": 10,
          "Order": 0,
          "Kind": "plc.fault.apply",
          "Target": "PLC1.DB1",
          "Payload": {
            "Id": "controller-flip",
            "Kind": "BitFlip",
            "StartMilliseconds": 10,
            "EndMilliseconds": 100,
            "Offset": 0,
            "Length": 1,
            "BitIndex": 0
          }
        },
        {
          "Id": "clear-fault-later",
          "AtMilliseconds": 900,
          "Order": 0,
          "Kind": "plc.fault.clear",
          "Target": "controller-flip",
          "Payload": {}
        }
      ],
      "Assertions": []
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
            ["SimulationRunRegistry:MaximumRuns"] = "1000",
            ["SimulationVirtualPlc:MaximumBlocks"] = "128",
            ["SimulationVirtualPlc:MaximumBlockBytes"] = "65536",
            ["SimulationVirtualPlc:MaximumOperationBytes"] = "65536",
            ["SimulationVirtualPlc:MaximumScenarioTransferBytes"] = "1536",
            ["SimulationVirtualPlc:MaximumFaults"] = "1024",
            ["SimulationVirtualPlc:MaximumFaultPayloadBytes"] = "1536",
            ["SimulationVirtualPlc:MaximumAuditRecords"] = "1000"
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
