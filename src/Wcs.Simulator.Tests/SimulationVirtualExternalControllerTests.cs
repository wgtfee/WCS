namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;

public sealed class SimulationVirtualExternalControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualExternalController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveRun_CanInspectEndpointsFaultsRequestsAndAudit()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var external = new SimulationVirtualExternalController(environment, configuration);
        var scenarioId = $"host-s5-{Guid.NewGuid():N}";
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

        for (var index = 0; index < 3; index++)
            Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));

        var statusResult = Assert.IsType<OkObjectResult>(external.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualExternalStatus>(statusResult.Value);
        Assert.Equal(1, status.EndpointCount);
        Assert.Equal(1, status.ActiveFaultCount);
        Assert.Equal(1, status.RequestCount);

        var endpointsResult = Assert.IsType<OkObjectResult>(external.ListEndpoints(run.RunId));
        var endpoints = Assert.IsAssignableFrom<IReadOnlyList<VirtualExternalEndpointSnapshot>>(endpointsResult.Value);
        Assert.Single(endpoints);
        Assert.Equal("MES1", endpoints[0].EndpointId);

        var faultsResult = Assert.IsType<OkObjectResult>(external.ListFaults(run.RunId));
        var faults = Assert.IsAssignableFrom<IReadOnlyList<VirtualExternalFaultSnapshot>>(faultsResult.Value);
        Assert.Single(faults);
        Assert.Equal(VirtualExternalFaultKind.Timeout, faults[0].Kind);

        var requestsResult = Assert.IsType<OkObjectResult>(external.ListRequests(run.RunId));
        var requests = Assert.IsAssignableFrom<IReadOnlyList<VirtualExternalRequestSnapshot>>(requestsResult.Value);
        var request = Assert.Single(requests);
        Assert.Equal(VirtualExternalRequestState.TimedOut, request.State);

        var requestResult = Assert.IsType<OkObjectResult>(external.GetRequest(run.RunId, request.RequestId));
        Assert.IsType<VirtualExternalRequestSnapshot>(requestResult.Value);

        var auditResult = Assert.IsType<OkObjectResult>(external.ListAudit(run.RunId, 20));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualExternalAuditRecord>>(auditResult.Value);
        Assert.Equal(3, audit.Count);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualExternalController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListEndpoints(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(
        string scenarioId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260801,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "host-s5-controller-test",
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
      "Seed": 20260801,
      "StartTimeUtc": "2026-08-01T00:00:00+00:00",
      "DurationMilliseconds": 1000,
      "StopOnAssertionFailure": true,
      "Actions": [
        { "Id":"endpoint", "AtMilliseconds":0, "Order":0, "Kind":"external.endpoint.define", "Target":"MES1", "Payload":{"Kind":"Mes"} },
        { "Id":"fault", "AtMilliseconds":0, "Order":1, "Kind":"external.fault.apply", "Target":"F1", "Payload":{"EndpointId":"MES1","Kind":"Timeout","StartsAtOffsetMilliseconds":0,"EndsAtOffsetMilliseconds":500} },
        { "Id":"invoke", "AtMilliseconds":0, "Order":2, "Kind":"external.request.invoke", "Target":"MES1", "Payload":{"Operation":"Order.Push","IdempotencyKey":"host-key","PayloadHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","MaxAttempts":1,"TimeoutMilliseconds":50} },
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
            ["SimulationVirtualExternal:MaximumEndpoints"] = "256",
            ["SimulationVirtualExternal:MaximumFaults"] = "2048",
            ["SimulationVirtualExternal:MaximumRequests"] = "10000",
            ["SimulationVirtualExternal:MaximumAuditRecords"] = "5000",
            ["SimulationVirtualExternal:MaximumRetryAttempts"] = "16",
            ["SimulationVirtualExternal:DefaultTimeoutMilliseconds"] = "5000",
            ["SimulationVirtualExternal:MaximumDelayMilliseconds"] = "86400000",
            ["SimulationVirtualExternal:CircuitFailureThreshold"] = "3",
            ["SimulationVirtualExternal:CircuitOpenMilliseconds"] = "30000"
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