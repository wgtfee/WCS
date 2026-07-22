using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportResilienceTests
{
    [Fact]
    public async Task InMemoryBackupStorage_TrimsOldestItems()
    {
        var storage = new InMemoryTransportLogicalBackupStorage();
        for (var index = 0; index < 3; index++)
        {
            await storage.SaveAsync(new TransportLogicalBackupManifest
            {
                BackupId = $"B{index}",
                FileName = $"B{index}.json",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(index)
            }, new byte[] { (byte)index });
        }

        var removed = await storage.TrimAsync(2);
        var remaining = await storage.GetManifestsAsync();

        Assert.Equal(1, removed);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, x => x.BackupId == "B0");
    }

    [Fact]
    public async Task CreateBackup_ProducesValidSha256Payload()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportResilienceService>();

        var manifest = await service.CreateBackupAsync("baseline", "test", "tester");
        var validation = await service.ValidateBackupAsync(manifest.BackupId);

        Assert.True(validation.HashValid);
        Assert.True(validation.SchemaValid);
        Assert.True(validation.PayloadReadable);
        Assert.True(validation.CanPrepareConfigurationRestore);
        Assert.Equal(64, manifest.Sha256.Length);
    }

    [Fact]
    public async Task ValidateBackup_RejectsTamperedPayload()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportResilienceService>();
        var storage = provider.GetRequiredService<ITransportLogicalBackupStorage>();
        var manifest = await service.CreateBackupAsync("baseline", "test", "tester");
        var content = Assert.IsType<TransportLogicalBackupContent>(await storage.LoadAsync(manifest.BackupId));
        var tampered = content.Payload.ToArray();
        tampered[0] = tampered[0] == 0 ? (byte)1 : (byte)(tampered[0] - 1);
        await storage.SaveAsync(content.Manifest, tampered);

        var validation = await service.ValidateBackupAsync(manifest.BackupId);

        Assert.False(validation.HashValid);
        Assert.Contains(validation.Issues, x => x.IssueType == TransportBackupValidationIssueType.HashMismatch);
    }

    [Fact]
    public async Task PrepareRestore_ImportsSnapshotWithoutApplyingRuntimeState()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportResilienceService>();
        var configuration = provider.GetRequiredService<ITransportConfigurationService>();
        var before = await configuration.GetAsync();
        var manifest = await service.CreateBackupAsync("baseline", "test", "tester");

        var result = await service.PrepareRestoreAsync(manifest.BackupId, "operator");
        var after = await configuration.GetAsync();

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.ImportedSnapshot);
        Assert.Equal(before, after);
        Assert.Contains(result.ManualRecoveryActions, x => x.Contains("活动任务", StringComparison.Ordinal));
        Assert.Contains(result.ManualRecoveryActions, x => x.Contains("PLC 点位映射", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsolatedDrill_DoesNotMutateVehicleRegistry()
    {
        using var provider = CreateProvider();
        var vehicles = provider.GetRequiredService<ITransportVehicleRegistry>();
        vehicles.Upsert(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Idle,
            CurrentNodeId = "N1",
            IsOnline = true,
            Version = 1
        });
        var before = Assert.Single(vehicles.GetAll());
        var service = provider.GetRequiredService<ITransportResilienceService>();

        var report = await service.RunDrillAsync(new TransportRecoveryDrillRequest
        {
            Scenario = TransportRecoveryDrillScenario.StateStoreUnavailable,
            Reason = "验证数据库不可用处置"
        }, "tester");
        var after = Assert.Single(vehicles.GetAll());

        Assert.True(report.IsIsolatedSimulation);
        Assert.True(report.Passed);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Preflight_ReportsMissingRealPlcDiagnosticAsCritical()
    {
        using var provider = CreateProvider();
        var configuration = provider.GetRequiredService<ITransportConfigurationService>();
        var saved = await configuration.SaveAndApplyAsync(new TransportRuntimeConfiguration
        {
            Vehicles = new[]
            {
                new TransportVehicleDefinition
                {
                    VehicleId = "EMS-01",
                    Kind = TransportVehicleKind.Ems,
                    InitialNodeId = "N1"
                }
            },
            Drivers = new[]
            {
                new TransportDriverEndpointDefinition
                {
                    DriverId = "DRV-01",
                    Kind = TransportVehicleKind.Ems,
                    Protocol = "S7",
                    Endpoint = "PLC1"
                }
            }
        }, 0, "tester");
        Assert.True(saved.Success, saved.Error);
        var mapService = provider.GetRequiredService<ITransportPlcSignalMapService>();
        var mapResult = await mapService.SaveAndApplyAsync(new TransportPlcSignalMap
        {
            VehicleId = "EMS-01",
            DriverId = "DRV-01",
            Kind = TransportVehicleKind.Ems,
            Mode = TransportDriverMode.PlcTag,
            HeartbeatTag = "heartbeat",
            CurrentNodeTag = "node",
            OperatingStateTag = "state",
            StateSequenceTag = "state-seq",
            CommandSequenceTag = "command-seq",
            CommandCodeTag = "command-code",
            CommandRequestTag = "command-request",
            AcknowledgedSequenceTag = "ack-seq",
            CommandAcceptedTag = "accepted",
            CommandCompletedTag = "completed"
        }, 0, "tester");
        Assert.True(mapResult.Success, mapResult.Error);
        var service = provider.GetRequiredService<ITransportResilienceService>();

        var report = await service.RunPreflightAsync();

        Assert.Contains(report.Checks, x =>
            x.CheckType == TransportReadinessCheckType.PlcDriverFreshness &&
            !x.Passed &&
            x.Severity == TransportReadinessSeverity.Critical);
        Assert.False(report.IsReady);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAlarmCenter>(new AlarmCenter(new EventBus()));
        services.AddUnifiedTransportScheduling();
        return services.BuildServiceProvider();
    }
}
