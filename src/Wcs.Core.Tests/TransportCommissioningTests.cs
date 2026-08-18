using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportCommissioningTests
{
    [Fact]
    public void PointTableImporter_Json_CreatesValidatedMap()
    {
        var importer = new TransportPointTableImporter();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[] { CreateRow() });

        var result = importer.Import(payload, "points.json");

        Assert.True(result.Success);
        var map = Assert.Single(result.Maps);
        Assert.Equal("EMS-01", map.VehicleId);
        Assert.Equal("DB100.Heartbeat", map.HeartbeatTag);
        Assert.Equal("N2", map.NodeCodeMap[20]);
        Assert.Equal(101, map.CommandCodeMap[TransportExecutionCommandType.MoveToNode]);
    }

    [Fact]
    public void PointTableImporter_Csv_RejectsDuplicateVehicle()
    {
        var importer = new TransportPointTableImporter();
        var header = "VehicleId,DriverId,Kind,Mode,HeartbeatTag,CurrentNodeTag,OperatingStateTag,StateSequenceTag,CommandSequenceTag,CommandCodeTag,CommandRequestTag,AcknowledgedSequenceTag,CommandAcceptedTag,CommandCompletedTag";
        var row = "EMS-01,DRV-01,Ems,PlcTag,hb,node,state,stateSeq,cmdSeq,cmdCode,cmdReq,ackSeq,accepted,completed";
        var csv = $"{header}\n{row}\n{row}";

        var result = importer.Import(Encoding.UTF8.GetBytes(csv), "points.csv");

        Assert.False(result.Success);
        Assert.Contains(result.Issues, x => x.Field == "VehicleId" && x.Message.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void PointTableImporter_Xlsx_ReadsFirstWorksheet()
    {
        var importer = new TransportPointTableImporter();

        var result = importer.Import(BuildXlsx(), "points.xlsx");

        Assert.True(result.Success, string.Join("; ", result.Issues.Select(x => x.Message)));
        Assert.Equal("EMS-XLSX", Assert.Single(result.Maps).VehicleId);
    }

    [Fact]
    public async Task ObservedAccessor_RecordsBatchReadLatency()
    {
        var fallback = new InMemoryTransportPlcAccessor();
        fallback.SetValue("DRV-01", "tag-1", 123);
        var maps = new InMemoryTransportPlcSignalMapRegistry();
        maps.Upsert(CreateMap());
        var traces = new InMemoryTransportCommunicationTraceStore();
        var inner = new HybridTransportPlcAccessor(new EmptyServiceProvider(), fallback);
        var observed = new TransportObservedPlcAccessor(inner, traces, maps);

        var values = await observed.ReadBatchAsync("DRV-01", new[] { "tag-1" });

        Assert.Equal(123, values["tag-1"]);
        var trace = Assert.Single(traces.GetRecent());
        Assert.Equal(TransportCommunicationOperation.BatchRead, trace.Operation);
        Assert.True(trace.Success);
        Assert.Equal("EMS-01", trace.VehicleId);
    }

    [Fact]
    public async Task CommissioningService_ReadWriteSignal_UsesMappedDriver()
    {
        var maps = new InMemoryTransportPlcSignalMapRegistry();
        maps.Upsert(CreateMap());
        var accessor = new InMemoryTransportPlcAccessor();
        var traces = new InMemoryTransportCommunicationTraceStore();
        var service = new TransportCommissioningService(maps, accessor, traces);

        var written = await service.WriteSignalAsync("EMS-01", "manual.tag", 88);
        var read = await service.ReadSignalAsync("EMS-01", "manual.tag");

        Assert.True(written.Success);
        Assert.True(read.Success);
        Assert.Equal(88, Convert.ToInt32(read.Value));
        Assert.Contains(traces.GetRecent(), x => x.Operation == TransportCommunicationOperation.SingleWrite);
        Assert.Contains(traces.GetRecent(), x => x.Operation == TransportCommunicationOperation.SingleRead);
    }

    [Fact]
    public async Task TemplateService_RejectsStaleVersionAndAppliesPrototype()
    {
        var store = new InMemoryTransportCommissioningStore();
        var mapStore = new InMemoryTransportPlcSignalMapStore();
        var registry = new InMemoryTransportPlcSignalMapRegistry();
        var mapService = new TransportPlcSignalMapService(mapStore, registry);
        var templates = new TransportSignalTemplateService(store, mapService);
        var template = new TransportSignalTemplate
        {
            TemplateId = "TPL-01",
            Name = "EMS 标准点位",
            Kind = TransportVehicleKind.Ems,
            MapPrototype = CreateMap() with { VehicleId = string.Empty, DriverId = string.Empty }
        };

        var first = await templates.SaveAsync(template, 0, "tester");
        var stale = await templates.SaveAsync(template with { Name = "stale" }, 0, "tester-2");
        var applied = await templates.ApplyAsync("TPL-01", "EMS-02", "DRV-02", 0, "tester");

        Assert.True(first.Success);
        Assert.True(stale.VersionConflict);
        Assert.True(applied.Success);
        Assert.True(registry.TryGet("EMS-02", out var map));
        Assert.Equal("DRV-02", map!.DriverId);
    }

    [Fact]
    public async Task RecoveryConflict_AcceptDeviceState_UpdatesPersistedVehicleOnly()
    {
        var commissioningStore = new InMemoryTransportCommissioningStore();
        var maps = new InMemoryTransportPlcSignalMapRegistry();
        maps.Upsert(CreateMap());
        var diagnostics = new TransportDriverDiagnosticsService();
        diagnostics.Upsert(new TransportDriverDiagnosticSnapshot
        {
            VehicleId = "EMS-01",
            DriverId = "DRV-01",
            Mode = TransportDriverMode.PlcTag,
            DeviceOnline = true,
            CurrentNodeId = "N2",
            OperatingState = TransportVehicleOperatingState.Idle,
            BatteryPercent = 77,
            LastReadAtUtc = DateTime.UtcNow
        });
        var vehicles = new InMemoryTransportVehicleRegistry();
        var stateStore = new InMemoryTransportStateStore();
        await stateStore.SaveVehicleAsync(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            CurrentNodeId = "N1",
            IsOnline = true,
            Version = 1
        });
        var caseValue = new TransportRecoveryConflictCase
        {
            CaseId = "CASE-01",
            VehicleId = "EMS-01",
            Decision = TransportDriverReconciliationDecision.PositionMismatch,
            PersistedNodeId = "N1",
            DeviceNodeId = "N2"
        };
        await commissioningStore.UpsertAsync(new TransportCommissioningRecord
        {
            Category = TransportCommissioningRecordCategory.RecoveryConflict,
            RecordId = caseValue.CaseId,
            PayloadJson = JsonSerializer.Serialize(caseValue)
        });
        var sync = new StubSynchronizationService();
        var service = new TransportRecoveryConflictService(
            commissioningStore,
            sync,
            diagnostics,
            maps,
            vehicles,
            stateStore);

        var result = await service.ResolveAsync(
            "CASE-01",
            TransportRecoveryResolution.AcceptDeviceState,
            "现场已核对编码器位置",
            "operator");

        Assert.True(result.Success);
        Assert.True(vehicles.TryGet("EMS-01", out var vehicle));
        Assert.Equal("N2", vehicle!.CurrentNodeId);
        Assert.Equal(77, vehicle.BatteryPercent);
        var persisted = await stateStore.LoadAsync();
        Assert.Equal("N2", Assert.Single(persisted.Vehicles).CurrentNodeId);
    }

    [Fact]
    public async Task Compensation_EvaluatesStopAsSafeAndMoveAsManual()
    {
        var stateStore = new InMemoryTransportStateStore();
        await stateStore.SaveCommandAsync(new TransportCommandRecord
        {
            CommandId = "STOP-01",
            RequestId = "REQ-01",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.Stop,
            Status = TransportCommandStatus.Sent
        });
        await stateStore.SaveCommandAsync(new TransportCommandRecord
        {
            CommandId = "MOVE-01",
            RequestId = "REQ-02",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.MoveToNode,
            Status = TransportCommandStatus.Sent
        });
        var diagnostics = new TransportDriverDiagnosticsService();
        diagnostics.Upsert(new TransportDriverDiagnosticSnapshot
        {
            VehicleId = "EMS-01",
            DriverId = "DRV-01",
            DeviceOnline = true
        });
        var maps = new InMemoryTransportPlcSignalMapRegistry();
        maps.Upsert(CreateMap());
        var resolver = new TransportDriverResolver(new ITransportVehicleDriver[]
        {
            new SimulatorTransportVehicleDriver(TransportVehicleKind.Ems),
            new SimulatorTransportVehicleDriver(TransportVehicleKind.Rgv)
        });
        var dispatcher = new TransportCommandDispatcher(resolver, stateStore);
        var service = new TransportCommandCompensationService(
            stateStore,
            diagnostics,
            maps,
            dispatcher,
            new InMemoryTransportCommunicationTraceStore());

        var report = await service.EvaluateAsync();

        Assert.Contains(report.Items, x => x.CommandId == "STOP-01" && x.Decision == TransportCommandCompensationDecision.SafeStopRetry);
        Assert.Contains(report.Items, x => x.CommandId == "MOVE-01" && x.Decision == TransportCommandCompensationDecision.RequiresManualConfirmation);
    }

    private static TransportPointTableRow CreateRow() => new()
    {
        VehicleId = "EMS-01",
        DriverId = "DRV-01",
        Kind = TransportVehicleKind.Ems,
        Mode = TransportDriverMode.PlcTag,
        HeartbeatTag = "DB100.Heartbeat",
        CurrentNodeTag = "DB100.Node",
        OperatingStateTag = "DB100.State",
        StateSequenceTag = "DB100.StateSequence",
        CommandSequenceTag = "DB101.CommandSequence",
        CommandCodeTag = "DB101.CommandCode",
        CommandRequestTag = "DB101.Request",
        AcknowledgedSequenceTag = "DB100.AckSequence",
        CommandAcceptedTag = "DB100.Accepted",
        CommandCompletedTag = "DB100.Completed",
        NodeCodeMapJson = "{\"10\":\"N1\",\"20\":\"N2\"}",
        TargetNodeCodeMapJson = "{\"N1\":10,\"N2\":20}",
        OperatingStateMapJson = "{\"1\":\"Idle\",\"2\":\"Executing\"}",
        CommandCodeMapJson = "{\"MoveToNode\":101,\"Stop\":199}"
    };

    private static TransportPlcSignalMap CreateMap() => new()
    {
        VehicleId = "EMS-01",
        DriverId = "DRV-01",
        Kind = TransportVehicleKind.Ems,
        Mode = TransportDriverMode.PlcTag,
        Enabled = true,
        HeartbeatTag = "heartbeat",
        CurrentNodeTag = "node",
        OperatingStateTag = "state",
        StateSequenceTag = "state.seq",
        CommandSequenceTag = "cmd.seq",
        CommandCodeTag = "cmd.code",
        CommandRequestTag = "cmd.request",
        AcknowledgedSequenceTag = "ack.seq",
        CommandAcceptedTag = "ack.accepted",
        CommandCompletedTag = "ack.completed",
        NodeCodeMap = new Dictionary<int, string> { [10] = "N1", [20] = "N2" },
        TargetNodeCodeMap = new Dictionary<string, int>(StringComparer.Ordinal) { ["N1"] = 10, ["N2"] = 20 }
    };

    private static byte[] BuildXlsx()
    {
        var headers = new[]
        {
            "VehicleId", "DriverId", "Kind", "Mode", "HeartbeatTag", "CurrentNodeTag",
            "OperatingStateTag", "StateSequenceTag", "CommandSequenceTag", "CommandCodeTag",
            "CommandRequestTag", "AcknowledgedSequenceTag", "CommandAcceptedTag", "CommandCompletedTag"
        };
        var values = new[]
        {
            "EMS-XLSX", "DRV-XLSX", "Ems", "PlcTag", "hb", "node", "state", "stateSeq",
            "cmdSeq", "cmdCode", "cmdReq", "ackSeq", "accepted", "completed"
        };
        var sheet = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                    RowXml(1, headers) + RowXml(2, values) + "</sheetData></worksheet>";
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(sheet);
        }
        return memory.ToArray();
    }

    private static string RowXml(int row, IReadOnlyList<string> values)
    {
        var cells = values.Select((value, index) =>
            $"<c r=\"{ColumnName(index)}{row}\" t=\"inlineStr\"><is><t>{value}</t></is></c>");
        return $"<row r=\"{row}\">{string.Concat(cells)}</row>";
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class StubSynchronizationService : ITransportDriverSynchronizationService
    {
        public Task<TransportDriverSyncReport> PollAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportDriverSyncReport());

        public Task<TransportDriverReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportDriverReconciliationReport());
    }
}
