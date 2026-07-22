namespace Wcs.Core.TransportScheduling;

using System.Diagnostics;
using System.Text.Json;

public sealed record TransportVersionedSaveResult<T>
{
    public bool Success { get; init; }
    public bool VersionConflict { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }

    public static TransportVersionedSaveResult<T> Saved(T value) =>
        new() { Success = true, Value = value };

    public static TransportVersionedSaveResult<T> Conflict(T? current) =>
        new() { VersionConflict = true, Value = current, Error = "版本已变化，请刷新后重试" };

    public static TransportVersionedSaveResult<T> Failed(string error) =>
        new() { Error = error };
}

public interface ITransportSignalTemplateService
{
    Task<IReadOnlyList<TransportSignalTemplate>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TransportVersionedSaveResult<TransportSignalTemplate>> SaveAsync(
        TransportSignalTemplate template,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string templateId, CancellationToken cancellationToken = default);
    Task<TransportPlcSignalMapSaveResult> ApplyAsync(
        string templateId,
        string vehicleId,
        string driverId,
        long expectedMapVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
}

public sealed class TransportSignalTemplateService : ITransportSignalTemplateService
{
    private readonly ITransportCommissioningStore _store;
    private readonly ITransportPlcSignalMapService _maps;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportSignalTemplateService(
        ITransportCommissioningStore store,
        ITransportPlcSignalMapService maps)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
    }

    public async Task<IReadOnlyList<TransportSignalTemplate>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(
            TransportCommissioningRecordCategory.SignalTemplate,
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(Deserialize<TransportSignalTemplate>)
            .Where(x => x is not null)
            .Cast<TransportSignalTemplate>()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<TransportVersionedSaveResult<TransportSignalTemplate>> SaveAsync(
        TransportSignalTemplate template,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.TemplateId))
            return TransportVersionedSaveResult<TransportSignalTemplate>.Failed("TemplateId 不能为空");
        if (string.IsNullOrWhiteSpace(template.Name))
            return TransportVersionedSaveResult<TransportSignalTemplate>.Failed("模板名称不能为空");
        if (string.IsNullOrWhiteSpace(template.Protocol))
            return TransportVersionedSaveResult<TransportSignalTemplate>.Failed("协议不能为空");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await GetAllAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => string.Equals(x.TemplateId, template.TemplateId, StringComparison.Ordinal));
            if ((current?.Version ?? 0) != expectedVersion)
                return TransportVersionedSaveResult<TransportSignalTemplate>.Conflict(current);

            var saved = template with
            {
                Version = expectedVersion + 1,
                UpdatedBy = updatedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await UpsertAsync(
                TransportCommissioningRecordCategory.SignalTemplate,
                saved.TemplateId,
                saved,
                cancellationToken).ConfigureAwait(false);
            return TransportVersionedSaveResult<TransportSignalTemplate>.Saved(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> DeleteAsync(
        string templateId,
        CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(
            TransportCommissioningRecordCategory.SignalTemplate,
            templateId,
            cancellationToken);

    public async Task<TransportPlcSignalMapSaveResult> ApplyAsync(
        string templateId,
        string vehicleId,
        string driverId,
        long expectedMapVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        var template = (await GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.TemplateId, templateId, StringComparison.Ordinal));
        if (template is null)
            return TransportPlcSignalMapSaveResult.Failed("点位模板不存在");
        if (string.IsNullOrWhiteSpace(vehicleId) || string.IsNullOrWhiteSpace(driverId))
            return TransportPlcSignalMapSaveResult.Failed("VehicleId 和 DriverId 不能为空");

        var map = template.MapPrototype with
        {
            VehicleId = vehicleId,
            DriverId = driverId,
            Kind = template.Kind,
            Version = expectedMapVersion,
            UpdatedBy = updatedBy,
            UpdatedAtUtc = DateTime.UtcNow
        };
        return await _maps.SaveAndApplyAsync(
            map,
            expectedMapVersion,
            updatedBy,
            cancellationToken).ConfigureAwait(false);
    }

    private Task UpsertAsync<T>(
        TransportCommissioningRecordCategory category,
        string recordId,
        T value,
        CancellationToken cancellationToken) =>
        _store.UpsertAsync(new TransportCommissioningRecord
        {
            Category = category,
            RecordId = recordId,
            PayloadJson = JsonSerializer.Serialize(value, TransportCommissioningJson.Options),
            UpdatedAtUtc = DateTime.UtcNow
        }, cancellationToken);

    private static T? Deserialize<T>(TransportCommissioningRecord record) =>
        JsonSerializer.Deserialize<T>(record.PayloadJson, TransportCommissioningJson.Options);
}

public interface ITransportCommissioningService
{
    Task<TransportSignalProbeResult> ProbeAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<TransportSignalValueResult> ReadSignalAsync(string vehicleId, string tag, CancellationToken cancellationToken = default);
    Task<TransportSignalValueResult> WriteSignalAsync(string vehicleId, string tag, object? value, CancellationToken cancellationToken = default);
    IReadOnlyList<TransportCommunicationTrace> GetTraces(int maxCount = 500, string? driverId = null, string? vehicleId = null);
}

public sealed class TransportCommissioningService : ITransportCommissioningService
{
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportPlcAccessor _accessor;
    private readonly ITransportCommunicationTraceStore _traces;

    public TransportCommissioningService(
        ITransportPlcSignalMapRegistry maps,
        ITransportPlcAccessor accessor,
        ITransportCommunicationTraceStore traces)
    {
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
    }

    public async Task<TransportSignalProbeResult> ProbeAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        var map = GetMap(vehicleId);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var connected = await _accessor.IsConnectedAsync(map.DriverId, cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                return new TransportSignalProbeResult
                {
                    VehicleId = vehicleId,
                    DriverId = map.DriverId,
                    Connected = false,
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Error = "PLC 驱动未连接"
                };
            }

            var values = await _accessor.ReadBatchAsync(
                map.DriverId,
                GetConfiguredTags(map),
                cancellationToken).ConfigureAwait(false);
            return new TransportSignalProbeResult
            {
                VehicleId = vehicleId,
                DriverId = map.DriverId,
                Connected = true,
                Values = values,
                DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TransportSignalProbeResult
            {
                VehicleId = vehicleId,
                DriverId = map.DriverId,
                Connected = false,
                DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    public async Task<TransportSignalValueResult> ReadSignalAsync(
        string vehicleId,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var map = GetMap(vehicleId);
        ValidateTag(tag);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var values = await _accessor.ReadBatchAsync(
                map.DriverId,
                new[] { tag },
                cancellationToken).ConfigureAwait(false);
            values.TryGetValue(tag, out var value);
            var result = Success(vehicleId, map.DriverId, tag, value, started);
            AppendSingleTrace(result, TransportCommunicationOperation.SingleRead);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var result = Failure(vehicleId, map.DriverId, tag, ex.Message, started);
            AppendSingleTrace(result, TransportCommunicationOperation.SingleRead);
            return result;
        }
    }

    public async Task<TransportSignalValueResult> WriteSignalAsync(
        string vehicleId,
        string tag,
        object? value,
        CancellationToken cancellationToken = default)
    {
        var map = GetMap(vehicleId);
        ValidateTag(tag);
        var normalized = NormalizeJsonValue(value);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _accessor.WriteBatchAsync(
                map.DriverId,
                new Dictionary<string, object?>(StringComparer.Ordinal) { [tag] = normalized },
                cancellationToken).ConfigureAwait(false);
            var result = Success(vehicleId, map.DriverId, tag, normalized, started);
            AppendSingleTrace(result, TransportCommunicationOperation.SingleWrite);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var result = Failure(vehicleId, map.DriverId, tag, ex.Message, started);
            AppendSingleTrace(result, TransportCommunicationOperation.SingleWrite);
            return result;
        }
    }

    public IReadOnlyList<TransportCommunicationTrace> GetTraces(
        int maxCount = 500,
        string? driverId = null,
        string? vehicleId = null) =>
        _traces.GetRecent(maxCount, driverId, vehicleId);

    private TransportPlcSignalMap GetMap(string vehicleId)
    {
        if (!_maps.TryGet(vehicleId, out var map) || map is null || !map.Enabled)
            throw new InvalidOperationException($"车辆 {vehicleId} 没有启用的点位映射");
        return map;
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag 不能为空", nameof(tag));
        if (tag.Length > 256)
            throw new ArgumentException("Tag 长度不能超过 256", nameof(tag));
    }

    private static IReadOnlyCollection<string> GetConfiguredTags(TransportPlcSignalMap map) =>
        new[]
        {
            map.HeartbeatTag, map.DeviceOnlineTag, map.CurrentNodeTag, map.OperatingStateTag,
            map.BatteryPercentTag, map.FaultCodeTag, map.FaultMessageTag, map.StateSequenceTag,
            map.ActiveCommandIdTag, map.LoadPresentTag, map.CommandIdTag, map.CommandSequenceTag,
            map.CommandCodeTag, map.TargetNodeTag, map.CommandRequestTag,
            map.AcknowledgedCommandIdTag, map.AcknowledgedSequenceTag, map.CommandAcceptedTag,
            map.CommandCompletedTag, map.CommandErrorTag
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is not JsonElement element)
            return value;
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            _ => element.GetRawText()
        };
    }

    private static TransportSignalValueResult Success(
        string vehicleId,
        string driverId,
        string tag,
        object? value,
        long started) => new()
    {
        VehicleId = vehicleId,
        DriverId = driverId,
        Tag = tag,
        Value = value,
        Success = true,
        DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
    };

    private static TransportSignalValueResult Failure(
        string vehicleId,
        string driverId,
        string tag,
        string error,
        long started) => new()
    {
        VehicleId = vehicleId,
        DriverId = driverId,
        Tag = tag,
        Success = false,
        Error = error,
        DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
    };

    private void AppendSingleTrace(
        TransportSignalValueResult result,
        TransportCommunicationOperation operation) =>
        _traces.Append(new TransportCommunicationTrace
        {
            DriverId = result.DriverId,
            VehicleId = result.VehicleId,
            Operation = operation,
            Tags = new[] { result.Tag },
            Success = result.Success,
            DurationMs = result.DurationMs,
            Error = result.Error
        });
}

public interface ITransportFaultCatalogService
{
    Task<IReadOnlyList<TransportFaultDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TransportVersionedSaveResult<TransportFaultDefinition>> SaveAsync(
        TransportFaultDefinition definition,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string definitionId, CancellationToken cancellationToken = default);
    Task<TransportFaultDefinition?> ResolveAsync(
        TransportVehicleKind kind,
        int faultCode,
        CancellationToken cancellationToken = default);
}

public sealed class TransportFaultCatalogService : ITransportFaultCatalogService
{
    private readonly ITransportCommissioningStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportFaultCatalogService(ITransportCommissioningStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<IReadOnlyList<TransportFaultDefinition>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(
            TransportCommissioningRecordCategory.FaultDefinition,
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(x => JsonSerializer.Deserialize<TransportFaultDefinition>(x.PayloadJson, TransportCommissioningJson.Options))
            .Where(x => x is not null)
            .Cast<TransportFaultDefinition>()
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.FaultCode)
            .ToArray();
    }

    public async Task<TransportVersionedSaveResult<TransportFaultDefinition>> SaveAsync(
        TransportFaultDefinition definition,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.FaultCode <= 0)
            return TransportVersionedSaveResult<TransportFaultDefinition>.Failed("故障码必须大于 0");
        if (string.IsNullOrWhiteSpace(definition.AlarmCode))
            return TransportVersionedSaveResult<TransportFaultDefinition>.Failed("AlarmCode 不能为空");
        if (string.IsNullOrWhiteSpace(definition.Message))
            return TransportVersionedSaveResult<TransportFaultDefinition>.Failed("故障说明不能为空");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
            var current = all.FirstOrDefault(x =>
                string.Equals(x.DefinitionId, definition.DefinitionId, StringComparison.Ordinal));
            if ((current?.Version ?? 0) != expectedVersion)
                return TransportVersionedSaveResult<TransportFaultDefinition>.Conflict(current);
            if (all.Any(x => x.Kind == definition.Kind &&
                             x.FaultCode == definition.FaultCode &&
                             !string.Equals(x.DefinitionId, definition.DefinitionId, StringComparison.Ordinal)))
            {
                return TransportVersionedSaveResult<TransportFaultDefinition>.Failed(
                    $"{definition.Kind} 故障码 {definition.FaultCode} 已存在");
            }

            var saved = definition with
            {
                Version = expectedVersion + 1,
                UpdatedBy = updatedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _store.UpsertAsync(new TransportCommissioningRecord
            {
                Category = TransportCommissioningRecordCategory.FaultDefinition,
                RecordId = saved.DefinitionId,
                PayloadJson = JsonSerializer.Serialize(saved, TransportCommissioningJson.Options),
                UpdatedAtUtc = saved.UpdatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
            return TransportVersionedSaveResult<TransportFaultDefinition>.Saved(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> DeleteAsync(
        string definitionId,
        CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(
            TransportCommissioningRecordCategory.FaultDefinition,
            definitionId,
            cancellationToken);

    public async Task<TransportFaultDefinition?> ResolveAsync(
        TransportVehicleKind kind,
        int faultCode,
        CancellationToken cancellationToken = default) =>
        (await GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => x.Enabled && x.Kind == kind && x.FaultCode == faultCode);
}

public sealed record TransportRecoveryConflictResult
{
    public bool Success { get; init; }
    public TransportRecoveryConflictCase? Case { get; init; }
    public string? Error { get; init; }

    public static TransportRecoveryConflictResult Ok(TransportRecoveryConflictCase value) =>
        new() { Success = true, Case = value };
    public static TransportRecoveryConflictResult Fail(string error, TransportRecoveryConflictCase? value = null) =>
        new() { Error = error, Case = value };
}

public interface ITransportRecoveryConflictService
{
    Task<IReadOnlyList<TransportRecoveryConflictCase>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportRecoveryConflictCase>> RefreshAsync(CancellationToken cancellationToken = default);
    Task<TransportRecoveryConflictResult> ResolveAsync(
        string caseId,
        TransportRecoveryResolution resolution,
        string reason,
        string resolvedBy,
        CancellationToken cancellationToken = default);
}

public sealed class TransportRecoveryConflictService : ITransportRecoveryConflictService
{
    private readonly ITransportCommissioningStore _store;
    private readonly ITransportDriverSynchronizationService _synchronization;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportStateStore _stateStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportRecoveryConflictService(
        ITransportCommissioningStore store,
        ITransportDriverSynchronizationService synchronization,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportPlcSignalMapRegistry maps,
        ITransportVehicleRegistry vehicles,
        ITransportStateStore stateStore)
    {
        _store = store;
        _synchronization = synchronization;
        _diagnostics = diagnostics;
        _maps = maps;
        _vehicles = vehicles;
        _stateStore = stateStore;
    }

    public async Task<IReadOnlyList<TransportRecoveryConflictCase>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(
            TransportCommissioningRecordCategory.RecoveryConflict,
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(x => JsonSerializer.Deserialize<TransportRecoveryConflictCase>(x.PayloadJson, TransportCommissioningJson.Options))
            .Where(x => x is not null)
            .Cast<TransportRecoveryConflictCase>()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<IReadOnlyList<TransportRecoveryConflictCase>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = await _synchronization.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            var existing = await GetAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in report.Items.Where(x => x.Decision != TransportDriverReconciliationDecision.InSync))
            {
                var current = existing.FirstOrDefault(x =>
                    x.State == TransportRecoveryConflictState.Pending &&
                    string.Equals(x.VehicleId, item.VehicleId, StringComparison.Ordinal) &&
                    x.Decision == item.Decision);
                var value = (current ?? new TransportRecoveryConflictCase()) with
                {
                    VehicleId = item.VehicleId,
                    Decision = item.Decision,
                    PersistedNodeId = item.PersistedNodeId,
                    DeviceNodeId = item.DeviceNodeId,
                    PersistedCommandId = item.PersistedCommandId,
                    DeviceCommandId = item.DeviceCommandId,
                    Message = item.Message,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await SaveCaseAsync(value, cancellationToken).ConfigureAwait(false);
            }
            return await GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportRecoveryConflictResult> ResolveAsync(
        string caseId,
        TransportRecoveryResolution resolution,
        string reason,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return TransportRecoveryConflictResult.Fail("冲突处置必须填写原因");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await GetAllAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => string.Equals(x.CaseId, caseId, StringComparison.Ordinal));
            if (current is null)
                return TransportRecoveryConflictResult.Fail("冲突记录不存在");
            if (current.State != TransportRecoveryConflictState.Pending)
                return TransportRecoveryConflictResult.Fail("冲突记录已经处置", current);

            var actionError = await ApplyResolutionAsync(current, resolution, cancellationToken).ConfigureAwait(false);
            if (actionError is not null)
                return TransportRecoveryConflictResult.Fail(actionError, current);

            var resolved = current with
            {
                State = TransportRecoveryConflictState.Resolved,
                Resolution = resolution,
                ResolutionReason = reason.Trim(),
                ResolvedBy = resolvedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await SaveCaseAsync(resolved, cancellationToken).ConfigureAwait(false);
            return TransportRecoveryConflictResult.Ok(resolved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ApplyResolutionAsync(
        TransportRecoveryConflictCase current,
        TransportRecoveryResolution resolution,
        CancellationToken cancellationToken)
    {
        switch (resolution)
        {
            case TransportRecoveryResolution.AcceptDeviceState:
                if (!_diagnostics.TryGet(current.VehicleId, out var diagnostic) || diagnostic is null)
                    return "没有可用的设备诊断快照";
                if (!diagnostic.DeviceOnline)
                    return "设备离线，不能采用设备状态";
                if (!_maps.TryGet(current.VehicleId, out var map) || map is null)
                    return "车辆点位映射不存在";
                _vehicles.TryGet(current.VehicleId, out var existingVehicle);
                var snapshot = new TransportVehicleSnapshot
                {
                    VehicleId = current.VehicleId,
                    Kind = map.Kind,
                    State = diagnostic.OperatingState,
                    CurrentNodeId = diagnostic.CurrentNodeId,
                    IsOnline = diagnostic.DeviceOnline,
                    BatteryPercent = Math.Clamp(diagnostic.BatteryPercent, 0, 100),
                    ActiveTaskCount = existingVehicle?.ActiveTaskCount ?? 0,
                    Capabilities = existingVehicle?.Capabilities ?? TransportVehicleCapability.All,
                    Version = (existingVehicle?.Version ?? 0) + 1,
                    UpdatedAtUtc = diagnostic.LastReadAtUtc ?? DateTime.UtcNow
                };
                _vehicles.Upsert(snapshot);
                await _stateStore.SaveVehicleAsync(snapshot, cancellationToken).ConfigureAwait(false);
                return null;

            case TransportRecoveryResolution.FailPersistedCommand:
                if (string.IsNullOrWhiteSpace(current.PersistedCommandId))
                    return "冲突记录没有持久化命令";
                var runtime = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                var command = runtime.Commands.FirstOrDefault(x =>
                    string.Equals(x.CommandId, current.PersistedCommandId, StringComparison.Ordinal));
                if (command is null)
                    return "持久化命令不存在";
                await _stateStore.SaveCommandAsync(command with
                {
                    Status = TransportCommandStatus.Failed,
                    Error = "现场人工确认后终止历史命令",
                    UpdatedAtUtc = DateTime.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                return null;

            case TransportRecoveryResolution.KeepPersistedState:
            case TransportRecoveryResolution.MarkFieldVerified:
                return null;

            default:
                return "不支持的冲突处置方式";
        }
    }

    private Task SaveCaseAsync(
        TransportRecoveryConflictCase value,
        CancellationToken cancellationToken) =>
        _store.UpsertAsync(new TransportCommissioningRecord
        {
            Category = TransportCommissioningRecordCategory.RecoveryConflict,
            RecordId = value.CaseId,
            PayloadJson = JsonSerializer.Serialize(value, TransportCommissioningJson.Options),
            UpdatedAtUtc = value.UpdatedAtUtc
        }, cancellationToken);
}

public interface ITransportCommandCompensationService
{
    Task<TransportCommandCompensationReport> EvaluateAsync(CancellationToken cancellationToken = default);
    Task<TransportCommandRecord> RetrySafeStopAsync(string commandId, CancellationToken cancellationToken = default);
}

public sealed class TransportCommandCompensationService : ITransportCommandCompensationService
{
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportCommandDispatcher _dispatcher;
    private readonly ITransportCommunicationTraceStore _traces;

    public TransportCommandCompensationService(
        ITransportStateStore stateStore,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportPlcSignalMapRegistry maps,
        ITransportCommandDispatcher dispatcher,
        ITransportCommunicationTraceStore traces)
    {
        _stateStore = stateStore;
        _diagnostics = diagnostics;
        _maps = maps;
        _dispatcher = dispatcher;
        _traces = traces;
    }

    public async Task<TransportCommandCompensationReport> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var items = runtime.Commands
            .Where(x => x.Status is TransportCommandStatus.Pending or TransportCommandStatus.Sent or TransportCommandStatus.Acknowledged or TransportCommandStatus.TimedOut)
            .Select(Evaluate)
            .ToArray();
        return new TransportCommandCompensationReport { Items = items };
    }

    public async Task<TransportCommandRecord> RetrySafeStopAsync(
        string commandId,
        CancellationToken cancellationToken = default)
    {
        var runtime = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var record = runtime.Commands.FirstOrDefault(x => string.Equals(x.CommandId, commandId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("补偿命令不存在");
        if (record.CommandType != TransportExecutionCommandType.Stop)
            throw new InvalidOperationException("只有 Stop 命令允许自动补偿，运动/装卸命令必须人工确认");
        if (!_diagnostics.TryGet(record.VehicleId, out var diagnostic) || diagnostic is null || !diagnostic.DeviceOnline)
            throw new InvalidOperationException("车辆离线，不能执行 Stop 补偿");
        if (!_maps.TryGet(record.VehicleId, out var map) || map is null || !map.Enabled)
            throw new InvalidOperationException("车辆点位映射不存在或已停用");

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _dispatcher.DispatchAsync(new TransportExecutionCommand
            {
                CommandId = record.CommandId,
                RequestId = record.RequestId,
                VehicleId = record.VehicleId,
                CommandType = record.CommandType,
                TargetNodeId = record.TargetNodeId,
                CreatedAtUtc = record.CreatedAtUtc
            }, map.Kind, maxRetries: 0, cancellationToken).ConfigureAwait(false);
            AppendTrace(map, result.Status is TransportCommandStatus.Acknowledged or TransportCommandStatus.Completed, started, result.Error);
            return result;
        }
        catch (Exception ex)
        {
            AppendTrace(map, false, started, ex.Message);
            throw;
        }
    }

    private TransportCommandCompensationItem Evaluate(TransportCommandRecord command)
    {
        if (!_diagnostics.TryGet(command.VehicleId, out var diagnostic) || diagnostic is null || !diagnostic.DeviceOnline)
        {
            return Item(command, TransportCommandCompensationDecision.WaitForReconnect, "车辆离线，等待重连后重新评估");
        }
        if (command.CommandType == TransportExecutionCommandType.Stop)
        {
            return Item(command, TransportCommandCompensationDecision.SafeStopRetry, "Stop 为幂等安全命令，可在审批后补偿重试");
        }
        return Item(command, TransportCommandCompensationDecision.RequiresManualConfirmation,
            "运动、装载或卸载命令的物理结果未知，禁止自动重发");
    }

    private static TransportCommandCompensationItem Item(
        TransportCommandRecord command,
        TransportCommandCompensationDecision decision,
        string message) => new()
    {
        CommandId = command.CommandId,
        VehicleId = command.VehicleId,
        CommandType = command.CommandType,
        CurrentStatus = command.Status,
        Decision = decision,
        Message = message
    };

    private void AppendTrace(
        TransportPlcSignalMap map,
        bool success,
        long started,
        string? error) =>
        _traces.Append(new TransportCommunicationTrace
        {
            DriverId = map.DriverId,
            VehicleId = map.VehicleId,
            Operation = TransportCommunicationOperation.CommandCompensation,
            Tags = new[] { map.CommandRequestTag },
            Success = success,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Error = error
        });
}
