namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualTraffic;

public sealed class SimulationVirtualTrafficControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualTrafficController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveRun_CanInspectReservationsWaitGraphDeadlocksAndAudit()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var traffic = new SimulationVirtualTrafficController(environment, configuration);
        var scenarioId = $"host-s4-{Guid.NewGuid():N}";
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

        for (var index = 0; index < 11; index++)
            Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));

        var statusResult = Assert.IsType<OkObjectResult>(traffic.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualTrafficStatus>(statusResult.Value);
        Assert.Equal(2, status.ZoneCount);
        Assert.Equal(2, status.ActiveReservationCount);
        Assert.Equal(2, status.WaitingRequestCount);
        Assert.Equal(2, status.WaitEdgeCount);
        Assert.Equal(1, status.ActiveDeadlockCount);

        var zonesResult = Assert.IsType<OkObjectResult>(traffic.ListZones(run.RunId));
        var zones = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficZoneSnapshot>>(zonesResult.Value);
        Assert.Equal(2, zones.Count);

        var reservationsResult = Assert.IsType<OkObjectResult>(traffic.ListReservations(run.RunId));
        var reservations = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficReservationSnapshot>>(reservationsResult.Value);
        Assert.Equal(2, reservations.Count);

        var waitingResult = Assert.IsType<OkObjectResult>(traffic.ListWaitingRequests(run.RunId));
        var waiting = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficWaitingRequestSnapshot>>(waitingResult.Value);
        Assert.Equal(2, waiting.Count);

        var graphResult = Assert.IsType<OkObjectResult>(traffic.ListWaitGraph(run.RunId));
        var graph = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficWaitEdge>>(graphResult.Value);
        Assert.Contains(graph, edge => edge.WaitingVehicleId == "RGV1" && edge.BlockingVehicleId == "RGV2");
        Assert.Contains(graph, edge => edge.WaitingVehicleId == "RGV2" && edge.BlockingVehicleId == "RGV1");

        var deadlocksResult = Assert.IsType<OkObjectResult>(traffic.ListDeadlocks(run.RunId));
        var deadlocks = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficDeadlockSnapshot>>(deadlocksResult.Value);
        var deadlock = Assert.Single(deadlocks);
        Assert.Equal("RGV2", deadlock.VictimVehicleId);

        var detailResult = Assert.IsType<OkObjectResult>(traffic.GetDeadlock(run.RunId, deadlock.DeadlockId));
        Assert.Equal(deadlock, Assert.IsType<VirtualTrafficDeadlockSnapshot>(detailResult.Value));

        var auditResult = Assert.IsType<OkObjectResult>(traffic.ListAudit(run.RunId, 20));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualTrafficAuditRecord>>(auditResult.Value);
        Assert.Equal(7, audit.Count);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualTrafficController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListZones(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260731,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "host-s4-controller-test",
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
      "Seed": 20260731,
      "StartTimeUtc": "2026-07-31T00:00:00+00:00",
      "DurationMilliseconds": 1000,
      "StopOnAssertionFailure": true,
      "Actions": [
        { "Id":"s1", "AtMilliseconds":0, "Order":0, "Kind":"rgv.segment.define", "Target":"S1", "Payload":{"FromNodeId":"N1","ToNodeId":"N2","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000} },
        { "Id":"s2", "AtMilliseconds":0, "Order":1, "Kind":"rgv.segment.define", "Target":"S2", "Payload":{"FromNodeId":"N2","ToNodeId":"N1","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000} },
        { "Id":"v1", "AtMilliseconds":0, "Order":2, "Kind":"rgv.vehicle.define", "Target":"RGV1", "Payload":{"InitialNodeId":"N1","SpeedMillimetersPerSecond":1000} },
        { "Id":"v2", "AtMilliseconds":0, "Order":3, "Kind":"rgv.vehicle.define", "Target":"RGV2", "Payload":{"InitialNodeId":"N2","SpeedMillimetersPerSecond":1000} },
        { "Id":"z1", "AtMilliseconds":0, "Order":4, "Kind":"traffic.zone.define", "Target":"Z1", "Payload":{"SegmentIds":["S1"],"Capacity":1,"Kind":"SharedSegment"} },
        { "Id":"z2", "AtMilliseconds":0, "Order":5, "Kind":"traffic.zone.define", "Target":"Z2", "Payload":{"SegmentIds":["S2"],"Capacity":1,"Kind":"OpposingDirection"} },
        { "Id":"h1", "AtMilliseconds":0, "Order":6, "Kind":"traffic.reserve", "Target":"RGV1", "Payload":{"SegmentId":"S1","Priority":10,"LeaseMilliseconds":10000} },
        { "Id":"h2", "AtMilliseconds":0, "Order":7, "Kind":"traffic.reserve", "Target":"RGV2", "Payload":{"SegmentId":"S2","Priority":20,"LeaseMilliseconds":10000} },
        { "Id":"w1", "AtMilliseconds":10, "Order":0, "Kind":"traffic.reserve", "Target":"RGV1", "Payload":{"SegmentId":"S2","Priority":10,"LeaseMilliseconds":10000} },
        { "Id":"w2", "AtMilliseconds":10, "Order":1, "Kind":"traffic.reserve", "Target":"RGV2", "Payload":{"SegmentId":"S1","Priority":20,"LeaseMilliseconds":10000} },
        { "Id":"detect", "AtMilliseconds":20, "Order":0, "Kind":"traffic.deadlock.detect", "Target":"all", "Payload":{} },
        { "Id":"future", "AtMilliseconds":1000, "Order":0, "Kind":"event.emit", "Target":"keep-active", "Payload":{} }
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
            ["SimulationVirtualPlc:MaximumAuditRecords"] = "1000",
            ["SimulationVirtualRgv:MaximumVehicles"] = "256",
            ["SimulationVirtualRgv:MaximumSegments"] = "2048",
            ["SimulationVirtualRgv:MaximumRouteSegments"] = "256",
            ["SimulationVirtualRgv:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualRgv:MaximumSegmentLengthMillimeters"] = "10000000",
            ["SimulationVirtualRgv:MaximumSpeedMillimetersPerSecond"] = "20000",
            ["SimulationVirtualRgv:BatteryDrainBasisPointsPerMeter"] = "1",
            ["SimulationVirtualTraffic:MaximumZones"] = "256",
            ["SimulationVirtualTraffic:MaximumSegmentsPerZone"] = "16",
            ["SimulationVirtualTraffic:MaximumReservations"] = "2048",
            ["SimulationVirtualTraffic:MaximumWaitingRequests"] = "2048",
            ["SimulationVirtualTraffic:MaximumDeadlocks"] = "512",
            ["SimulationVirtualTraffic:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualTraffic:MaximumRollingLookAheadSegments"] = "16",
            ["SimulationVirtualTraffic:DefaultReservationLeaseMilliseconds"] = "60000",
            ["SimulationVirtualTraffic:MaximumReservationLeaseMilliseconds"] = "86400000"
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
