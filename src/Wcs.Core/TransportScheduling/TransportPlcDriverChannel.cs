namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.PlcSubsystem.Abstractions;

public interface ITransportPlcAccessor
{
    Task<bool> IsConnectedAsync(string driverId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        string driverId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default);
    Task WriteBatchAsync(
        string driverId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
}

/// <summary>使用现有 IPlcClient 的标签批量读写能力接入真实 PLC。</summary>
public sealed class PlcClientTransportPlcAccessor : ITransportPlcAccessor
{
    private readonly IPlcClient _client;

    public PlcClientTransportPlcAccessor(IPlcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<bool> IsConnectedAsync(string driverId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        string driverId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = tags.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        var values = await _client.ReadBatchAsync(names).ConfigureAwait(false);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
            result[names[i]] = i < values.Length ? values[i] : null;
        return result;
    }

    public async Task WriteBatchAsync(
        string driverId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _client.WriteBatchAsync(values.Select(x => (x.Key, x.Value))).ConfigureAwait(false);
    }
}

/// <summary>
/// 运行时优先使用 DI 中的真实 IPlcClient；模拟模式或未注册 PLC 客户端时回退到内存访问器。
/// 这样同一套 Host 可以通过 Simulator.Enabled 在真实与离线模式之间切换。
/// </summary>
public sealed class HybridTransportPlcAccessor : ITransportPlcAccessor
{
    private readonly IServiceProvider _services;
    private readonly InMemoryTransportPlcAccessor _fallback;

    public HybridTransportPlcAccessor(IServiceProvider services, InMemoryTransportPlcAccessor fallback)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public Task<bool> IsConnectedAsync(string driverId, CancellationToken cancellationToken = default) =>
        Resolve() is { } real
            ? real.IsConnectedAsync(driverId, cancellationToken)
            : _fallback.IsConnectedAsync(driverId, cancellationToken);

    public Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        string driverId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        Resolve() is { } real
            ? real.ReadBatchAsync(driverId, tags, cancellationToken)
            : _fallback.ReadBatchAsync(driverId, tags, cancellationToken);

    public Task WriteBatchAsync(
        string driverId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default) =>
        Resolve() is { } real
            ? real.WriteBatchAsync(driverId, values, cancellationToken)
            : _fallback.WriteBatchAsync(driverId, values, cancellationToken);

    private PlcClientTransportPlcAccessor? Resolve()
    {
        var client = _services.GetService<IPlcClient>();
        return client is null ? null : new PlcClientTransportPlcAccessor(client);
    }
}

/// <summary>CI、离线联调与异常注入使用的可控 PLC 标签存储。</summary>
public sealed class InMemoryTransportPlcAccessor : ITransportPlcAccessor
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _connections = new(StringComparer.Ordinal);

    public bool FailNextRead { get; set; }
    public bool FailNextWrite { get; set; }

    public Task<bool> IsConnectedAsync(string driverId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_connections.GetValueOrDefault(driverId, true));
    }

    public Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        string driverId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextRead)
        {
            FailNextRead = false;
            throw new IOException("模拟 PLC 读取失败");
        }

        IReadOnlyDictionary<string, object?> result = tags
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(x => x, x => _values.GetValueOrDefault(Key(driverId, x)), StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    public Task WriteBatchAsync(
        string driverId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new IOException("模拟 PLC 写入失败");
        }

        foreach (var (tag, value) in values)
            _values[Key(driverId, tag)] = value;
        return Task.CompletedTask;
    }

    public void SetConnected(string driverId, bool connected) => _connections[driverId] = connected;
    public void SetValue(string driverId, string tag, object? value) => _values[Key(driverId, tag)] = value;
    public object? GetValue(string driverId, string tag) => _values.GetValueOrDefault(Key(driverId, tag));

    private static string Key(string driverId, string tag) => $"{driverId}\u001f{tag}";
}

/// <summary>
/// 将统一运输命令映射为 PLC 标签握手，并把 PLC 状态批量还原为协议状态帧。
/// 写入顺序固定为“参数/序号 → 请求位”，确认后由 WCS 清除请求位。
/// </summary>
public sealed class TransportPlcDriverChannel : ITransportDriverChannel
{
    private sealed record HeartbeatTracker(string Value, DateTime ChangedAtUtc);

    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportPlcAccessor _accessor;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, TransportProtocolCommandFrame>> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HeartbeatTracker> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _clearedRequests = new(StringComparer.Ordinal);

    public TransportPlcDriverChannel(
        ITransportPlcSignalMapRegistry maps,
        ITransportPlcAccessor accessor,
        ITransportDriverDiagnosticsService diagnostics)
    {
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async Task WriteCommandAsync(
        TransportProtocolCommandFrame command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var map = GetPlcMap(command.VehicleId);
        if (!await _accessor.IsConnectedAsync(map.DriverId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"驱动 {map.DriverId} 未连接");

        var commandCode = map.CommandCodeMap.TryGetValue(command.CommandType, out var configuredCode)
            ? configuredCode
            : (int)command.CommandType + 1;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [map.CommandSequenceTag] = command.Sequence,
            [map.CommandCodeTag] = commandCode
        };
        AddIfConfigured(payload, map.CommandIdTag, command.CommandId);
        if (!string.IsNullOrWhiteSpace(command.TargetNodeId))
        {
            object targetValue = map.TargetNodeCodeMap.TryGetValue(command.TargetNodeId, out var nodeCode)
                ? nodeCode
                : command.TargetNodeId;
            AddIfConfigured(payload, map.TargetNodeTag, targetValue);
        }

        var correlations = _commands.GetOrAdd(command.VehicleId, _ => new ConcurrentDictionary<long, TransportProtocolCommandFrame>());
        correlations[command.Sequence] = command;

        try
        {
            await _accessor.WriteBatchAsync(map.DriverId, payload, cancellationToken).ConfigureAwait(false);
            await _accessor.WriteBatchAsync(
                map.DriverId,
                new Dictionary<string, object?> { [map.CommandRequestTag] = true },
                cancellationToken).ConfigureAwait(false);

            _diagnostics.TryGet(command.VehicleId, out var current);
            _diagnostics.Upsert((current ?? EmptyDiagnostic(map)) with
            {
                PendingCommandId = command.CommandId,
                PendingSequence = command.Sequence,
                LastWriteAtUtc = DateTime.UtcNow,
                LastError = null
            });
        }
        catch (Exception ex)
        {
            correlations.TryRemove(command.Sequence, out _);
            RecordFailure(map, ex.Message);
            throw;
        }
    }

    public async Task<TransportProtocolStateFrame> ReadStateAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        var map = GetPlcMap(vehicleId);
        var now = DateTime.UtcNow;
        try
        {
            var connected = await _accessor.IsConnectedAsync(map.DriverId, cancellationToken).ConfigureAwait(false);
            if (!connected)
                return Offline(map, "PLC 连接不可用", now);

            var values = await _accessor.ReadBatchAsync(
                map.DriverId,
                ReadTags(map),
                cancellationToken).ConfigureAwait(false);

            var heartbeatAt = ResolveHeartbeat(map, values, now);
            var heartbeatAlive = now - heartbeatAt <= TimeSpan.FromMilliseconds(map.HeartbeatTimeoutMs);
            var deviceOnline = ReadBool(values, map.DeviceOnlineTag, true) && heartbeatAlive;
            var stateCode = ReadInt(values, map.OperatingStateTag, (int)TransportVehicleOperatingState.Idle);
            var operatingState = ResolveOperatingState(map, stateCode);
            var faultCode = ReadInt(values, map.FaultCodeTag, 0);
            var faultMessage = ReadString(values, map.FaultMessageTag);
            if (faultCode != 0)
                operatingState = TransportVehicleOperatingState.Faulted;
            if (!deviceOnline)
                operatingState = TransportVehicleOperatingState.Offline;

            var stateSequence = ReadLong(values, map.StateSequenceTag, 0);
            var acknowledgedSequence = ReadLong(values, map.AcknowledgedSequenceTag, 0);
            var acknowledgedCommandId = ReadString(values, map.AcknowledgedCommandIdTag)
                ?? ResolveCorrelatedCommandId(vehicleId, acknowledgedSequence);
            var activeCommandId = ReadString(values, map.ActiveCommandIdTag)
                ?? acknowledgedCommandId;
            var accepted = ReadBool(values, map.CommandAcceptedTag, acknowledgedSequence > 0);
            var completed = ReadBool(values, map.CommandCompletedTag, false);
            var commandError = ReadString(values, map.CommandErrorTag)
                ?? (faultCode == 0 ? null : faultMessage ?? $"PLC 故障码 {faultCode}");
            var currentNode = ResolveNode(map, Get(values, map.CurrentNodeTag));
            var battery = Math.Clamp(ReadInt(values, map.BatteryPercentTag, 100), 0, 100);
            var loadPresent = ReadBool(values, map.LoadPresentTag, false);

            if (acknowledgedSequence > 0 && !string.IsNullOrWhiteSpace(map.CommandRequestTag))
                await ClearRequestOnceAsync(map, acknowledgedSequence, cancellationToken).ConfigureAwait(false);

            var pending = ResolvePending(vehicleId, acknowledgedSequence);
            var frame = new TransportProtocolStateFrame
            {
                VehicleId = vehicleId,
                DeviceOnline = deviceOnline,
                CurrentNodeId = currentNode,
                OperatingState = operatingState,
                ActiveCommandId = activeCommandId,
                StateSequence = stateSequence,
                AcknowledgedCommandId = acknowledgedCommandId,
                AcknowledgedSequence = acknowledgedSequence,
                CommandAccepted = accepted,
                CommandCompleted = completed,
                CommandError = commandError,
                BatteryPercent = battery,
                FaultCode = faultCode,
                FaultMessage = faultMessage,
                LoadPresent = loadPresent,
                HeartbeatAtUtc = heartbeatAt
            };

            _diagnostics.TryGet(vehicleId, out var previous);
            _diagnostics.Upsert(new TransportDriverDiagnosticSnapshot
            {
                VehicleId = vehicleId,
                DriverId = map.DriverId,
                Mode = map.Mode,
                AccessorConnected = connected,
                DeviceOnline = deviceOnline,
                CurrentNodeId = currentNode,
                OperatingState = operatingState,
                BatteryPercent = battery,
                FaultCode = faultCode,
                FaultMessage = faultMessage,
                LoadPresent = loadPresent,
                HeartbeatAtUtc = heartbeatAt,
                StateSequence = stateSequence,
                AcknowledgedSequence = acknowledgedSequence,
                AcknowledgedCommandId = acknowledgedCommandId,
                PendingCommandId = pending?.CommandId,
                PendingSequence = pending?.Sequence ?? 0,
                LastReadAtUtc = now,
                LastWriteAtUtc = previous?.LastWriteAtUtc,
                ConsecutiveReadFailures = 0,
                LastError = null
            });

            CleanupCorrelations(vehicleId, acknowledgedSequence);
            return frame;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Offline(map, ex.Message, now);
        }
    }

    private TransportProtocolStateFrame Offline(TransportPlcSignalMap map, string error, DateTime now)
    {
        RecordFailure(map, error);
        return new TransportProtocolStateFrame
        {
            VehicleId = map.VehicleId,
            DeviceOnline = false,
            OperatingState = TransportVehicleOperatingState.Offline,
            HeartbeatAtUtc = _heartbeats.GetValueOrDefault(map.VehicleId)?.ChangedAtUtc ?? now,
            CommandError = error
        };
    }

    private void RecordFailure(TransportPlcSignalMap map, string error)
    {
        _diagnostics.TryGet(map.VehicleId, out var previous);
        _diagnostics.Upsert((previous ?? EmptyDiagnostic(map)) with
        {
            AccessorConnected = false,
            DeviceOnline = false,
            OperatingState = TransportVehicleOperatingState.Offline,
            LastReadAtUtc = DateTime.UtcNow,
            ConsecutiveReadFailures = (previous?.ConsecutiveReadFailures ?? 0) + 1,
            LastError = error
        });
    }

    private async Task ClearRequestOnceAsync(
        TransportPlcSignalMap map,
        long acknowledgedSequence,
        CancellationToken cancellationToken)
    {
        var key = $"{map.VehicleId}:{acknowledgedSequence}";
        if (!_clearedRequests.TryAdd(key, 0))
            return;
        await _accessor.WriteBatchAsync(
            map.DriverId,
            new Dictionary<string, object?> { [map.CommandRequestTag] = false },
            cancellationToken).ConfigureAwait(false);
    }

    private DateTime ResolveHeartbeat(
        TransportPlcSignalMap map,
        IReadOnlyDictionary<string, object?> values,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(map.HeartbeatTag))
            return now;

        var value = Convert.ToString(Get(values, map.HeartbeatTag), CultureInfo.InvariantCulture) ?? string.Empty;
        var tracker = _heartbeats.AddOrUpdate(
            map.VehicleId,
            _ => new HeartbeatTracker(value, now),
            (_, current) => string.Equals(current.Value, value, StringComparison.Ordinal)
                ? current
                : new HeartbeatTracker(value, now));
        return tracker.ChangedAtUtc;
    }

    private TransportProtocolCommandFrame? ResolvePending(string vehicleId, long acknowledgedSequence)
    {
        if (!_commands.TryGetValue(vehicleId, out var commands))
            return null;
        return commands.Values
            .Where(x => x.Sequence > acknowledgedSequence)
            .OrderBy(x => x.Sequence)
            .FirstOrDefault();
    }

    private string? ResolveCorrelatedCommandId(string vehicleId, long sequence)
    {
        if (sequence <= 0 || !_commands.TryGetValue(vehicleId, out var commands))
            return null;
        return commands.TryGetValue(sequence, out var command) ? command.CommandId : null;
    }

    private void CleanupCorrelations(string vehicleId, long acknowledgedSequence)
    {
        if (acknowledgedSequence <= 0 || !_commands.TryGetValue(vehicleId, out var commands))
            return;
        foreach (var sequence in commands.Keys.Where(x => x < acknowledgedSequence).ToArray())
            commands.TryRemove(sequence, out _);
    }

    private TransportPlcSignalMap GetPlcMap(string vehicleId)
    {
        if (!_maps.TryGet(vehicleId, out var map) || map is null || !map.Enabled || map.Mode != TransportDriverMode.PlcTag)
            throw new InvalidOperationException($"车辆 {vehicleId} 未配置启用的 PLC 标签映射");
        return map;
    }

    private static IReadOnlyCollection<string> ReadTags(TransportPlcSignalMap map) =>
        new[]
        {
            map.HeartbeatTag, map.DeviceOnlineTag, map.CurrentNodeTag, map.OperatingStateTag,
            map.BatteryPercentTag, map.FaultCodeTag, map.FaultMessageTag, map.StateSequenceTag,
            map.ActiveCommandIdTag, map.LoadPresentTag, map.AcknowledgedCommandIdTag,
            map.AcknowledgedSequenceTag, map.CommandAcceptedTag, map.CommandCompletedTag,
            map.CommandErrorTag
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

    private static TransportVehicleOperatingState ResolveOperatingState(TransportPlcSignalMap map, int code)
    {
        if (map.OperatingStateMap.TryGetValue(code, out var mapped))
            return mapped;
        return Enum.IsDefined(typeof(TransportVehicleOperatingState), code)
            ? (TransportVehicleOperatingState)code
            : TransportVehicleOperatingState.Faulted;
    }

    private static string ResolveNode(TransportPlcSignalMap map, object? value)
    {
        var code = ToNullableInt(value);
        if (code.HasValue && map.NodeCodeMap.TryGetValue(code.Value, out var nodeId))
            return nodeId;
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static TransportDriverDiagnosticSnapshot EmptyDiagnostic(TransportPlcSignalMap map) => new()
    {
        VehicleId = map.VehicleId,
        DriverId = map.DriverId,
        Mode = map.Mode,
        OperatingState = TransportVehicleOperatingState.Offline
    };

    private static void AddIfConfigured(IDictionary<string, object?> values, string tag, object? value)
    {
        if (!string.IsNullOrWhiteSpace(tag))
            values[tag] = value;
    }

    private static object? Get(IReadOnlyDictionary<string, object?> values, string tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : values.GetValueOrDefault(tag);

    private static string? ReadString(IReadOnlyDictionary<string, object?> values, string tag) =>
        Convert.ToString(Get(values, tag), CultureInfo.InvariantCulture);

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string tag, bool defaultValue)
    {
        var value = Get(values, tag);
        if (value is null) return defaultValue;
        if (value is bool boolean) return boolean;
        if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)) return parsed;
        return ToNullableInt(value) is > 0;
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> values, string tag, int defaultValue) =>
        ToNullableInt(Get(values, tag)) ?? defaultValue;

    private static long ReadLong(IReadOnlyDictionary<string, object?> values, string tag, long defaultValue)
    {
        var value = Get(values, tag);
        if (value is null) return defaultValue;
        try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }

    private static int? ToNullableInt(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}
