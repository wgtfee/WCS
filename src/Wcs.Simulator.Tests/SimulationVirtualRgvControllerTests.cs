namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Core.TransportScheduling;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;

public sealed class SimulationVirtualRgvControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualRgvController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveRun_CanInspectVehiclesSegmentsOccupancyAndTransportSnapshot()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var virtualRgv = new SimulationVirtualRgvController(environment, configuration);
        var scenarioId = $"host-s3-{Guid.NewGuid():N}";
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

        for (var index = 0; index < 4; index++)
            Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));

        var statusResult = Assert.IsType<OkObjectResult>(virtualRgv.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualRgvStatus>(statusResult.Value);
        Assert.Equal(1, status.VehicleCount);
        Assert.Equal(1, status.SegmentCount);
        Assert.Equal(1, status.ExecutingVehicleCount);
        Assert.Equal(1, status.OccupiedSegmentCount);

        var vehiclesResult = Assert.IsType<OkObjectResult>(virtualRgv.ListVehicles(run.RunId));
        var vehicles = Assert.IsAssignableFrom<IReadOnlyList<VirtualRgvVehicleSnapshot>>(vehiclesResult.Value);
        var vehicle = Assert.Single(vehicles);
        Assert.Equal("RGV1", vehicle.VehicleId);
        Assert.Equal("S1", vehicle.CurrentSegmentId);
        Assert.Equal(500, vehicle.SegmentProgressMillimeters);

        var segmentResult = Assert.IsType<OkObjectResult>(virtualRgv.GetSegment(run.RunId, "S1"));
        var segment = Assert.IsType<VirtualRgvSegmentSnapshot>(segmentResult.Value);
        Assert.Equal("N1", segment.FromNodeId);
        Assert.Equal("N2", segment.ToNodeId);

        var occupancyResult = Assert.IsType<OkObjectResult>(virtualRgv.ListOccupancy(run.RunId));
        var occupancy = Assert.IsAssignableFrom<IReadOnlyList<VirtualRgvSegmentOccupancy>>(occupancyResult.Value);
        Assert.Equal(["RGV1"], Assert.Single(occupancy).VehicleIds);

        var transportResult = Assert.IsType<OkObjectResult>(
            virtualRgv.GetTransportSnapshot(run.RunId, "RGV1"));
        var transport = Assert.IsType<TransportVehicleSnapshot>(transportResult.Value);
        Assert.Equal(TransportVehicleKind.Rgv, transport.Kind);
        Assert.Equal(TransportVehicleOperatingState.Executing, transport.State);
        Assert.Equal(1, transport.ActiveTaskCount);

        var auditResult = Assert.IsType<OkObjectResult>(virtualRgv.ListAudit(run.RunId, 10));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualRgvAuditRecord>>(auditResult.Value);
        Assert.Equal(4, audit.Count);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualRgvController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListVehicles(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260730,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "host-s3-controller-test",
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
      "Seed": 20260730,
      "StartTimeUtc": "2026-07-30T00:00:00+00:00",
      "DurationMilliseconds": 1000,
      "StopOnAssertionFailure": true,
      "Actions": [
        {
          "Id": "segment",
          "AtMilliseconds": 0,
          "Order": 0,
          "Kind": "rgv.segment.define",
          "Target": "S1",
          "Payload": {
            "FromNodeId": "N1",
            "ToNodeId": "N2",
            "LengthMillimeters": 1000,
            "SpeedLimitMillimetersPerSecond": 1000
          }
        },
        {
          "Id": "vehicle",
          "AtMilliseconds": 0,
          "Order": 1,
          "Kind": "rgv.vehicle.define",
          "Target": "RGV1",
          "Payload": {
            "InitialNodeId": "N1",
            "SpeedMillimetersPerSecond": 1000,
            "BatteryPercent": 100,
            "IsOnline": true,
            "Capabilities": "Carry"
          }
        },
        {
          "Id": "route",
          "AtMilliseconds": 0,
          "Order": 2,
          "Kind": "rgv.route.assign",
          "Target": "RGV1",
          "Payload": { "SegmentIds": ["S1"] }
        },
        {
          "Id": "advance",
          "AtMilliseconds": 500,
          "Order": 0,
          "Kind": "rgv.vehicle.advance",
          "Target": "RGV1",
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
            ["SimulationGovernance:AllowedEnvironments:0"] = "Simulation",
            ["SimulationGovernance:AllowedEnvironments:1"] = "SimulationLoadTest",
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
            ["SimulationVirtualPlc:MaximumAuditRecords"] = "1000",
            ["SimulationVirtualRgv:MaximumVehicles"] = "256",
            ["SimulationVirtualRgv:MaximumSegments"] = "2048",
            ["SimulationVirtualRgv:MaximumRouteSegments"] = "256",
            ["SimulationVirtualRgv:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualRgv:MaximumSegmentLengthMillimeters"] = "10000000",
            ["SimulationVirtualRgv:MaximumSpeedMillimetersPerSecond"] = "20000",
            ["SimulationVirtualRgv:BatteryDrainBasisPointsPerMeter"] = "1"
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
