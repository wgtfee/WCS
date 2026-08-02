namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualIntegration;

public sealed class SimulationVirtualIntegrationControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualIntegrationController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task CompletedRun_CanInspectMissionConsistencyAndAudit()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var integration = new SimulationVirtualIntegrationController(environment, configuration);
        var scenarioId = $"host-s7-{Guid.NewGuid():N}";
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
        Assert.IsType<OkObjectResult>(scenarios.Resume(run.RunId));
        var completed = Assert.IsType<OkObjectResult>(await scenarios.RunToCompletion(run.RunId, CancellationToken.None));
        var snapshot = Assert.IsType<SimulationRunSnapshot>(completed.Value);
        Assert.Equal(SimulationSessionStatus.Completed, snapshot.Status);

        var statusResult = Assert.IsType<OkObjectResult>(integration.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualIntegrationStatus>(statusResult.Value);
        Assert.Equal(1, status.MissionCount);
        Assert.Equal(1, status.AcknowledgedCount);

        var missionResult = Assert.IsType<OkObjectResult>(integration.GetMission(run.RunId, "M1"));
        var mission = Assert.IsType<VirtualIntegrationMissionSnapshot>(missionResult.Value);
        Assert.Equal(VirtualIntegrationMissionState.Acknowledged, mission.State);

        var consistencyResult = Assert.IsType<OkObjectResult>(integration.GetConsistency(run.RunId, "M1"));
        var consistency = Assert.IsType<VirtualIntegrationConsistencySnapshot>(consistencyResult.Value);
        Assert.True(consistency.IsConsistent, consistency.Detail);
        Assert.True(consistency.ExternalExactlyOnce);
        Assert.True(consistency.HealthOutcomeExactlyOnce);

        var auditResult = Assert.IsType<OkObjectResult>(integration.ListAudit(run.RunId, 20));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualIntegrationAuditRecord>>(auditResult.Value);
        Assert.True(audit.Count >= 6);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualIntegrationController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListMissions(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260802,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "host-s7-controller-test",
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
      "Seed": 20260802,
      "StartTimeUtc": "2026-08-02T00:00:00+00:00",
      "DurationMilliseconds": 2300,
      "StopOnAssertionFailure": true,
      "Actions": [
        {
          "Id":"define", "AtMilliseconds":0, "Order":0, "Kind":"integration.mission.define", "Target":"M1",
          "Payload":{
            "PlcBlockKey":"PLC1.DB100", "VehicleId":"RGV1", "LoadId":"LOAD1",
            "SourceNodeId":"N1", "DestinationNodeId":"N3", "ExternalEndpointId":"MES1",
            "ExternalSystemKind":"Mes", "HealthAssetId":"ASSET1", "Priority":100,
            "VehicleSpeedMillimetersPerSecond":1000, "VehicleBatteryPercent":100,
            "InitialHealthScore":95, "InitialFusionRiskScore":0.05,
            "Segments":[
              {"SegmentId":"S1","FromNodeId":"N1","ToNodeId":"N2","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000},
              {"SegmentId":"S2","FromNodeId":"N2","ToNodeId":"N3","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000}
            ]
          }
        },
        { "Id":"dispatch", "AtMilliseconds":10, "Order":0, "Kind":"integration.mission.dispatch", "Target":"M1", "Payload":{} },
        { "Id":"advance-1", "AtMilliseconds":1010, "Order":0, "Kind":"integration.mission.advance", "Target":"M1", "Payload":{} },
        { "Id":"advance-2", "AtMilliseconds":2010, "Order":0, "Kind":"integration.mission.advance", "Target":"M1", "Payload":{} },
        { "Id":"ack-1", "AtMilliseconds":2100, "Order":0, "Kind":"integration.mission.ack", "Target":"M1", "Payload":{} },
        { "Id":"ack-2", "AtMilliseconds":2200, "Order":0, "Kind":"integration.mission.ack", "Target":"M1", "Payload":{} }
      ],
      "Assertions": [
        { "Id":"state", "AtMilliseconds":2300, "Order":0, "Kind":"integration.mission.state", "Target":"M1", "Expected":"Acknowledged" },
        { "Id":"consistent", "AtMilliseconds":2300, "Order":1, "Kind":"integration.mission.consistent", "Target":"M1", "Expected":true },
        { "Id":"once", "AtMilliseconds":2300, "Order":2, "Kind":"integration.external.exactly-once", "Target":"M1", "Expected":true }
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
            ["SimulationScenarioEngine:MaximumStateEntries"] = "50000",
            ["SimulationScenarioEngine:MaximumStateValueCharacters"] = "16384",
            ["SimulationScenarioEngine:MaximumCheckpointBytes"] = "67108864",
            ["SimulationScenarioEngine:MaximumSpeedFactor"] = "1000",
            ["SimulationRunRegistry:MaximumRuns"] = "1000",
            ["SimulationVirtualPlc:MaximumBlocks"] = "128",
            ["SimulationVirtualPlc:MaximumBlockBytes"] = "65536",
            ["SimulationVirtualPlc:MaximumOperationBytes"] = "65536",
            ["SimulationVirtualPlc:MaximumScenarioTransferBytes"] = "1536",
            ["SimulationVirtualPlc:MaximumFaults"] = "1024",
            ["SimulationVirtualPlc:MaximumFaultPayloadBytes"] = "1536",
            ["SimulationVirtualPlc:MaximumAuditRecords"] = "2000",
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
            ["SimulationVirtualTraffic:MaximumReservationLeaseMilliseconds"] = "86400000",
            ["SimulationVirtualExternal:MaximumEndpoints"] = "256",
            ["SimulationVirtualExternal:MaximumFaults"] = "2048",
            ["SimulationVirtualExternal:MaximumRequests"] = "10000",
            ["SimulationVirtualExternal:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualExternal:MaximumRetryAttempts"] = "16",
            ["SimulationVirtualExternal:DefaultTimeoutMilliseconds"] = "5000",
            ["SimulationVirtualExternal:MaximumDelayMilliseconds"] = "86400000",
            ["SimulationVirtualExternal:CircuitFailureThreshold"] = "3",
            ["SimulationVirtualExternal:CircuitOpenMilliseconds"] = "30000",
            ["SimulationVirtualHealth:MaximumAssets"] = "256",
            ["SimulationVirtualHealth:MaximumSamplesPerAsset"] = "2048",
            ["SimulationVirtualHealth:MaximumForecastsPerAsset"] = "512",
            ["SimulationVirtualHealth:MaximumOutcomesPerAsset"] = "128",
            ["SimulationVirtualHealth:MaximumGeneratedSamplesPerAction"] = "1024",
            ["SimulationVirtualHealth:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualHealth:ForecastMinimumHistoryPoints"] = "48",
            ["SimulationVirtualHealth:ForecastMinimumHistorySpanHours"] = "24",
            ["SimulationVirtualHealth:ForecastMaximumHistoryPoints"] = "2000",
            ["SimulationVirtualHealth:TrendWindowSize"] = "12",
            ["SimulationVirtualHealth:TrendChangeThreshold"] = "2",
            ["SimulationVirtualHealth:HealthyMinimumScore"] = "85",
            ["SimulationVirtualHealth:AttentionMinimumScore"] = "70",
            ["SimulationVirtualHealth:DegradedMinimumScore"] = "40",
            ["SimulationVirtualHealth:MaximumRulHours"] = "17520",
            ["SimulationVirtualIntegration:MaximumMissions"] = "256",
            ["SimulationVirtualIntegration:MaximumSegmentsPerMission"] = "16",
            ["SimulationVirtualIntegration:MaximumAuditRecords"] = "10000",
            ["SimulationVirtualIntegration:ReservationLeaseMilliseconds"] = "60000",
            ["SimulationVirtualIntegration:ExternalAckMaximumAttempts"] = "3",
            ["SimulationVirtualIntegration:ExternalAckTimeoutMilliseconds"] = "5000",
            ["SimulationVirtualIntegration:ExternalAckRetryDelayMilliseconds"] = "1000"
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
