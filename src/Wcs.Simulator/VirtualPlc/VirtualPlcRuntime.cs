namespace Wcs.Simulator.VirtualPlc;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Simulator.ScenarioEngine;

/// <summary>
/// Deterministic, process-local PLC memory backed by the scenario state store.
/// Every block, connection flag, fault, operation sequence and audit record is
/// therefore included in the existing S1 checkpoint and replay hash.
/// </summary>
public sealed partial class VirtualPlcRuntime
{
    private const int ByteChunkSize = 1_536;
    private const int IndexChunkSize = 16;
    private const string BlockIndexName = "blocks";
    private const string FaultIndexName = "faults";
    private const string OperationSequenceKey = "__vplc.operationSequence";
    private const string AuditCountKey = "__vplc.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualPlcOptions _options;
    private readonly ulong _deterministicSalt;

    public VirtualPlcRuntime(
        SimulationStateStore state,
        VirtualPlcOptions options,
        ulong deterministicSalt = 0)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _deterministicSalt = deterministicSalt;
    }

    [GeneratedRegex("^(?<plc>[A-Za-z0-9][A-Za-z0-9_-]{0,63})\\.DB(?<db>[0-9]{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlcNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex FaultIdRegex();

    public VirtualPlcBlockSnapshot DefineBlock(
        string blockKey,
        int size,
        ReadOnlySpan<byte> initialBytes,
        long virtualOffsetMilliseconds = 0,
        DateTimeOffset? occurredAtUtc = null)
    {
        var parsed = ParseBlockKey(blockKey);
        if (size < 1 || size > _options.MaximumBlockBytes)
            throw new InvalidOperationException($"Virtual PLC block size must be between 1 and {_options.MaximumBlockBytes} bytes.");
        if (initialBytes.Length > size)
            throw new InvalidOperationException("Initial virtual PLC bytes exceed the declared block size.");
        if (TryReadBlockMeta(parsed.BlockKey, out _))
            throw new InvalidOperationException($"Virtual PLC block '{parsed.BlockKey}' is already defined.");

        var blocks = ReadIndex(BlockIndexName).ToList();
        if (blocks.Count >= _options.MaximumBlocks)
            throw new InvalidOperationException("Virtual PLC has reached MaximumBlocks.");

        var data = new byte[size];
        initialBytes.CopyTo(data);
        WriteBlockMeta(new BlockMeta(parsed.BlockKey, parsed.PlcName, parsed.DbNumber, size));
        WriteBytes(BlockDataPrefix(parsed.BlockKey), data);
        blocks.Add(parsed.BlockKey);
        WriteIndex(BlockIndexName, blocks.OrderBy(static value => value, StringComparer.Ordinal).ToArray());

        if (!_state.Contains(ConnectionKey(parsed.PlcName)))
            SetJson(ConnectionKey(parsed.PlcName), true);

        var sequence = NextOperationSequence();
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc ?? DateTimeOffset.UnixEpoch.AddMilliseconds(virtualOffsetMilliseconds),
            virtualOffsetMilliseconds,
            "define",
            parsed.BlockKey,
            true,
            false,
            null,
            0,
            size,
            EmptyHash,
            ComputeSha256(data),
            []));

        return ToBlockSnapshot(ReadRequiredBlockMeta(parsed.BlockKey), data);
    }

    public VirtualPlcBlockSnapshot GetBlock(string blockKey)
    {
        var parsed = ParseBlockKey(blockKey);
        var meta = ReadRequiredBlockMeta(parsed.BlockKey);
        var data = ReadBytes(BlockDataPrefix(parsed.BlockKey), meta.Size);
        return ToBlockSnapshot(meta, data);
    }

    public IReadOnlyList<string> ListBlocks() => ReadIndex(BlockIndexName);

    public bool IsConnected(string plcName, long virtualOffsetMilliseconds)
    {
        var normalized = NormalizePlcName(plcName);
        if (!ReadConnectionFlag(normalized))
            return false;
        return !GetActiveFaults(virtualOffsetMilliseconds)
            .Any(fault => fault.Kind == VirtualPlcFaultKind.Disconnect && TargetMatchesPlc(fault.Target, normalized));
    }

    public void SetConnection(
        string plcName,
        bool connected,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var normalized = NormalizePlcName(plcName);
        SetJson(ConnectionKey(normalized), connected);
        var sequence = NextOperationSequence();
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            "connection.set",
            normalized,
            true,
            false,
            null,
            0,
            0,
            EmptyHash,
            EmptyHash,
            []));
    }

    public VirtualPlcFaultSnapshot ApplyFault(
        VirtualPlcFaultDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateFaultId(definition.Id);
        if (definition.StartMilliseconds < 0)
            throw new InvalidOperationException("Virtual PLC fault start cannot be negative.");
        if (definition.EndMilliseconds is { } end && end < definition.StartMilliseconds)
            throw new InvalidOperationException("Virtual PLC fault end cannot be before its start.");

        var target = NormalizeTarget(definition.Target);
        var blockTarget = TryParseBlockKey(target, out var blockIdentity);
        if (RequiresBlockTarget(definition.Kind) && !blockTarget)
            throw new InvalidOperationException($"Virtual PLC fault kind '{definition.Kind}' requires a DB block target.");
        if (blockTarget && !TryReadBlockMeta(blockIdentity.BlockKey, out var blockMeta))
            throw new KeyNotFoundException($"Virtual PLC block '{blockIdentity.BlockKey}' was not found.");

        if (definition.Offset < 0 || definition.Length < 1 || definition.Length > _options.MaximumFaultPayloadBytes)
            throw new InvalidOperationException("Virtual PLC fault offset/length is outside the configured fault payload limit.");
        if (blockTarget && checked(definition.Offset + definition.Length) > blockMeta!.Size)
            throw new InvalidOperationException("Virtual PLC fault range exceeds the target block.");
        if (definition.Kind == VirtualPlcFaultKind.BitFlip && definition.BitIndex is < 0 or > 7)
            throw new InvalidOperationException("Virtual PLC BitFlip fault BitIndex must be between 0 and 7.");
        if (definition.Kind == VirtualPlcFaultKind.Jitter &&
            (definition.JitterMinimum < -255 || definition.JitterMaximum > 255 || definition.JitterMinimum > definition.JitterMaximum))
            throw new InvalidOperationException("Virtual PLC Jitter bounds must be ordered and between -255 and 255.");
        if (definition.ReplacementBytes is { Length: > 0 } replacement && replacement.Length > _options.MaximumFaultPayloadBytes)
            throw new InvalidOperationException("Virtual PLC replacement bytes exceed MaximumFaultPayloadBytes.");

        var faults = ReadIndex(FaultIndexName).ToList();
        if (faults.Contains(definition.Id, StringComparer.Ordinal))
            throw new InvalidOperationException($"Virtual PLC fault '{definition.Id}' already exists.");
        if (faults.Count >= _options.MaximumFaults)
            throw new InvalidOperationException("Virtual PLC has reached MaximumFaults.");

        byte[]? frozenBytes = null;
        if (definition.Kind == VirtualPlcFaultKind.Stuck)
        {
            var block = GetBlock(blockIdentity.BlockKey);
            frozenBytes = block.Data.AsSpan(definition.Offset, definition.Length).ToArray();
        }

        byte[]? replacementBytes = definition.ReplacementBytes?.ToArray();
        if (definition.Kind == VirtualPlcFaultKind.OutOfRange && replacementBytes is null)
            replacementBytes = Enumerable.Repeat((byte)0xFF, definition.Length).ToArray();
        if (replacementBytes is { Length: > 0 } && replacementBytes.Length != definition.Length)
            throw new InvalidOperationException("Virtual PLC replacement byte length must equal the fault Length.");

        var stored = new FaultStorage(
            definition.Id,
            definition.Kind,
            target,
            definition.StartMilliseconds,
            definition.EndMilliseconds,
            definition.Offset,
            definition.Length,
            definition.BitIndex,
            definition.JitterMinimum,
            definition.JitterMaximum,
            replacementBytes,
            frozenBytes,
            true);
        SetJson(FaultKey(definition.Id), stored);
        faults.Add(definition.Id);
        WriteIndex(FaultIndexName, faults.OrderBy(static value => value, StringComparer.Ordinal).ToArray());

        var sequence = NextOperationSequence();
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            "fault.apply",
            target,
            true,
            false,
            null,
            definition.Offset,
            definition.Length,
            EmptyHash,
            EmptyHash,
            [definition.Id]));

        return ToFaultSnapshot(stored, virtualOffsetMilliseconds);
    }

    public VirtualPlcFaultSnapshot ClearFault(
        string faultId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ValidateFaultId(faultId);
        var stored = ReadRequiredFault(faultId);
        stored = stored with { Enabled = false };
        SetJson(FaultKey(faultId), stored);
        var sequence = NextOperationSequence();
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            "fault.clear",
            stored.Target,
            true,
            false,
            null,
            stored.Offset,
            stored.Length,
            EmptyHash,
            EmptyHash,
            [faultId]));
        return ToFaultSnapshot(stored, virtualOffsetMilliseconds);
    }

    public IReadOnlyList<VirtualPlcFaultSnapshot> ListFaults(long virtualOffsetMilliseconds) =>
        ReadIndex(FaultIndexName)
            .Select(ReadRequiredFault)
            .Select(fault => ToFaultSnapshot(fault, virtualOffsetMilliseconds))
            .OrderBy(static fault => fault.Id, StringComparer.Ordinal)
            .ToArray();

    public bool IsFaultActive(string faultId, long virtualOffsetMilliseconds) =>
        IsActive(ReadRequiredFault(faultId), virtualOffsetMilliseconds);

    public VirtualPlcOperationResult Read(
        string blockKey,
        int offset,
        int count,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var parsed = ParseBlockKey(blockKey);
        var meta = ReadRequiredBlockMeta(parsed.BlockKey);
        ValidateRange(meta, offset, count);
        var sequence = NextOperationSequence();
        var fullData = ReadBytes(BlockDataPrefix(parsed.BlockKey), meta.Size);
        var blockHash = ComputeSha256(fullData);
        var activeFaults = GetMatchingFaults(parsed.BlockKey, parsed.PlcName, virtualOffsetMilliseconds);
        var faultIds = activeFaults.Select(static fault => fault.Id).ToArray();

        if (!ReadConnectionFlag(parsed.PlcName) || activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.Disconnect))
            return CompleteFailure(sequence, "read", parsed.BlockKey, "Disconnected", "The virtual PLC is disconnected.", offset, count, blockHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc);
        if (activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.Timeout))
            return CompleteFailure(sequence, "read", parsed.BlockKey, "Timeout", "The virtual PLC read timed out.", offset, count, blockHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc, timedOut: true);
        if (activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.ReadFailure))
            return CompleteFailure(sequence, "read", parsed.BlockKey, "ReadFailure", "The virtual PLC read failed.", offset, count, blockHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc);

        var result = fullData.AsSpan(offset, count).ToArray();
        var applied = new List<string>();
        foreach (var fault in activeFaults.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            if (ApplyReadFault(fault, result, offset, sequence))
                applied.Add(fault.Id);
        }

        var operation = new VirtualPlcOperationResult(
            sequence,
            "read",
            parsed.BlockKey,
            true,
            false,
            null,
            null,
            offset,
            count,
            result,
            applied);
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            operation.Operation,
            operation.Target,
            true,
            false,
            null,
            offset,
            count,
            blockHash,
            blockHash,
            applied));
        return operation;
    }

    public VirtualPlcOperationResult Write(
        string blockKey,
        int offset,
        ReadOnlySpan<byte> bytes,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var parsed = ParseBlockKey(blockKey);
        var meta = ReadRequiredBlockMeta(parsed.BlockKey);
        ValidateRange(meta, offset, bytes.Length);
        var sequence = NextOperationSequence();
        var fullData = ReadBytes(BlockDataPrefix(parsed.BlockKey), meta.Size);
        var beforeHash = ComputeSha256(fullData);
        var activeFaults = GetMatchingFaults(parsed.BlockKey, parsed.PlcName, virtualOffsetMilliseconds);
        var faultIds = activeFaults.Select(static fault => fault.Id).ToArray();

        if (!ReadConnectionFlag(parsed.PlcName) || activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.Disconnect))
            return CompleteFailure(sequence, "write", parsed.BlockKey, "Disconnected", "The virtual PLC is disconnected.", offset, bytes.Length, beforeHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc);
        if (activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.Timeout))
            return CompleteFailure(sequence, "write", parsed.BlockKey, "Timeout", "The virtual PLC write timed out.", offset, bytes.Length, beforeHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc, timedOut: true);
        if (activeFaults.Any(static fault => fault.Kind == VirtualPlcFaultKind.WriteFailure))
            return CompleteFailure(sequence, "write", parsed.BlockKey, "WriteFailure", "The virtual PLC write failed.", offset, bytes.Length, beforeHash, faultIds, virtualOffsetMilliseconds, occurredAtUtc);

        bytes.CopyTo(fullData.AsSpan(offset, bytes.Length));
        WriteBytes(BlockDataPrefix(parsed.BlockKey), fullData);
        var afterHash = ComputeSha256(fullData);
        var operation = new VirtualPlcOperationResult(
            sequence,
            "write",
            parsed.BlockKey,
            true,
            false,
            null,
            null,
            offset,
            bytes.Length,
            [],
            []);
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            operation.Operation,
            operation.Target,
            true,
            false,
            null,
            offset,
            bytes.Length,
            beforeHash,
            afterHash,
            []));
        return operation;
    }

    public VirtualPlcStatusSnapshot GetStatus(long virtualOffsetMilliseconds)
    {
        var blocks = ListBlocks();
        var faults = ListFaults(virtualOffsetMilliseconds);
        var connections = blocks
            .Select(ParseBlockKey)
            .Select(static block => block.PlcName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static plc => plc, StringComparer.Ordinal)
            .ToDictionary(
                plc => plc,
                plc => IsConnected(plc, virtualOffsetMilliseconds),
                StringComparer.Ordinal);
        return new VirtualPlcStatusSnapshot(
            blocks.Count,
            faults.Count,
            faults.Count(static fault => fault.Active),
            checked((int)Math.Min(int.MaxValue, ReadLong(AuditCountKey))),
            ReadLong(OperationSequenceKey),
            connections,
            blocks);
    }

    public IReadOnlyList<VirtualPlcAuditRecord> ListAudit(int take = 100)
    {
        if (take < 1 || take > _options.MaximumAuditRecords)
            throw new InvalidOperationException("Virtual PLC audit take is outside MaximumAuditRecords.");
        var count = Math.Min(ReadLong(AuditCountKey), _options.MaximumAuditRecords);
        var next = ReadLong(OperationSequenceKey);
        var first = Math.Max(0, next - count);
        var records = new List<VirtualPlcAuditRecord>();
        for (var sequence = first; sequence < next; sequence++)
        {
            var slot = sequence % _options.MaximumAuditRecords;
            if (TryReadJson<VirtualPlcAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == sequence)
                records.Add(record);
        }
        return records
            .OrderByDescending(static record => record.Sequence)
            .Take(take)
            .ToArray();
    }

    private bool ApplyReadFault(FaultStorage fault, byte[] result, int readOffset, long sequence)
    {
        if (fault.Kind is not (VirtualPlcFaultKind.Stuck or VirtualPlcFaultKind.BitFlip or VirtualPlcFaultKind.Jitter or VirtualPlcFaultKind.OutOfRange))
            return false;

        var readEnd = checked(readOffset + result.Length);
        var faultEnd = checked(fault.Offset + fault.Length);
        var overlapStart = Math.Max(readOffset, fault.Offset);
        var overlapEnd = Math.Min(readEnd, faultEnd);
        if (overlapStart >= overlapEnd)
            return false;

        for (var blockOffset = overlapStart; blockOffset < overlapEnd; blockOffset++)
        {
            var resultIndex = blockOffset - readOffset;
            var faultIndex = blockOffset - fault.Offset;
            switch (fault.Kind)
            {
                case VirtualPlcFaultKind.Stuck:
                    result[resultIndex] = fault.FrozenBytes![faultIndex];
                    break;
                case VirtualPlcFaultKind.BitFlip:
                    result[resultIndex] ^= (byte)(1 << fault.BitIndex);
                    break;
                case VirtualPlcFaultKind.Jitter:
                    var delta = DeterministicDelta(fault, sequence, blockOffset);
                    result[resultIndex] = (byte)Math.Clamp(result[resultIndex] + delta, byte.MinValue, byte.MaxValue);
                    break;
                case VirtualPlcFaultKind.OutOfRange:
                    result[resultIndex] = fault.ReplacementBytes![faultIndex];
                    break;
            }
        }
        return true;
    }

    private int DeterministicDelta(FaultStorage fault, long sequence, int blockOffset)
    {
        var input = Encoding.UTF8.GetBytes($"{_deterministicSalt}:{sequence}:{fault.Id}:{blockOffset}");
        var hash = SHA256.HashData(input);
        var width = checked(fault.JitterMaximum - fault.JitterMinimum + 1);
        return fault.JitterMinimum + (BitConverter.ToUInt32(hash, 0) % width is var value ? checked((int)value) : 0);
    }

    private VirtualPlcOperationResult CompleteFailure(
        long sequence,
        string operation,
        string target,
        string errorCode,
        string errorMessage,
        int offset,
        int count,
        string blockHash,
        IReadOnlyList<string> faultIds,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        bool timedOut = false)
    {
        AppendAudit(new VirtualPlcAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            operation,
            target,
            false,
            timedOut,
            errorCode,
            offset,
            count,
            blockHash,
            blockHash,
            faultIds));
        return new VirtualPlcOperationResult(
            sequence,
            operation,
            target,
            false,
            timedOut,
            errorCode,
            errorMessage,
            offset,
            count,
            [],
            faultIds);
    }

    private IReadOnlyList<FaultStorage> GetMatchingFaults(
        string blockKey,
        string plcName,
        long virtualOffsetMilliseconds) =>
        GetActiveFaults(virtualOffsetMilliseconds)
            .Where(fault => string.Equals(fault.Target, blockKey, StringComparison.Ordinal) ||
                            string.Equals(fault.Target, plcName, StringComparison.Ordinal))
            .OrderBy(static fault => fault.Id, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<FaultStorage> GetActiveFaults(long virtualOffsetMilliseconds) =>
        ReadIndex(FaultIndexName)
            .Select(ReadRequiredFault)
            .Where(fault => IsActive(fault, virtualOffsetMilliseconds))
            .ToArray();

    private static bool IsActive(FaultStorage fault, long offset) =>
        fault.Enabled && offset >= fault.StartMilliseconds &&
        (fault.EndMilliseconds is null || offset <= fault.EndMilliseconds.Value);

    private static bool RequiresBlockTarget(VirtualPlcFaultKind kind) =>
        kind is VirtualPlcFaultKind.ReadFailure or VirtualPlcFaultKind.WriteFailure or
            VirtualPlcFaultKind.Stuck or VirtualPlcFaultKind.BitFlip or
            VirtualPlcFaultKind.Jitter or VirtualPlcFaultKind.OutOfRange;

    private static bool TargetMatchesPlc(string target, string plcName) =>
        string.Equals(target, plcName, StringComparison.Ordinal) ||
        target.StartsWith(plcName + ".DB", StringComparison.Ordinal);

    private void ValidateRange(BlockMeta meta, int offset, int count)
    {
        if (offset < 0 || count < 0 || count > _options.MaximumOperationBytes)
            throw new InvalidOperationException("Virtual PLC read/write range is outside MaximumOperationBytes.");
        if (checked(offset + count) > meta.Size)
            throw new InvalidOperationException("Virtual PLC read/write range exceeds the target block.");
    }

    private void AppendAudit(VirtualPlcAuditRecord record)
    {
        var slot = record.Sequence % _options.MaximumAuditRecords;
        SetJson(AuditSlotKey(slot), record);
        var count = Math.Min(_options.MaximumAuditRecords, checked((int)ReadLong(AuditCountKey) + 1));
        SetJson(AuditCountKey, count);
    }

    private long NextOperationSequence()
    {
        var updated = _state.Increment(OperationSequenceKey, 1);
        return updated - 1;
    }

    private bool ReadConnectionFlag(string plcName) =>
        !TryReadJson<bool>(ConnectionKey(plcName), out var connected) || connected;

    private BlockMeta ReadRequiredBlockMeta(string blockKey) =>
        TryReadBlockMeta(blockKey, out var meta)
            ? meta
            : throw new KeyNotFoundException($"Virtual PLC block '{blockKey}' was not found.");

    private bool TryReadBlockMeta(string blockKey, out BlockMeta meta) =>
        TryReadJson(BlockMetaKey(blockKey), out meta!);

    private void WriteBlockMeta(BlockMeta meta) => SetJson(BlockMetaKey(meta.BlockKey), meta);

    private FaultStorage ReadRequiredFault(string faultId) =>
        TryReadJson<FaultStorage>(FaultKey(faultId), out var fault)
            ? fault
            : throw new KeyNotFoundException($"Virtual PLC fault '{faultId}' was not found.");

    private void WriteBytes(string prefix, ReadOnlySpan<byte> bytes)
    {
        var chunkCount = (bytes.Length + ByteChunkSize - 1) / ByteChunkSize;
        SetJson(prefix + ".meta", new ByteStorageMeta(bytes.Length, chunkCount));
        for (var index = 0; index < chunkCount; index++)
        {
            var start = index * ByteChunkSize;
            var length = Math.Min(ByteChunkSize, bytes.Length - start);
            SetJson(prefix + $".chunk.{index:D4}", Convert.ToBase64String(bytes.Slice(start, length)));
        }
    }

    private byte[] ReadBytes(string prefix, int expectedLength)
    {
        var meta = ReadRequiredJson<ByteStorageMeta>(prefix + ".meta");
        if (meta.Length != expectedLength)
            throw new InvalidOperationException("Virtual PLC byte storage length does not match its metadata.");
        var data = new byte[meta.Length];
        var offset = 0;
        for (var index = 0; index < meta.ChunkCount; index++)
        {
            var encoded = ReadRequiredJson<string>(prefix + $".chunk.{index:D4}");
            var chunk = Convert.FromBase64String(encoded);
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }
        if (offset != data.Length)
            throw new InvalidOperationException("Virtual PLC byte storage is incomplete.");
        return data;
    }

    private IReadOnlyList<string> ReadIndex(string name)
    {
        if (!TryReadJson<IndexMeta>(IndexMetaKey(name), out var meta))
            return [];
        var result = new List<string>(meta.Count);
        for (var index = 0; index < meta.ChunkCount; index++)
            result.AddRange(ReadRequiredJson<string[]>(IndexChunkKey(name, index)));
        if (result.Count != meta.Count)
            throw new InvalidOperationException($"Virtual PLC {name} index is inconsistent.");
        return result;
    }

    private void WriteIndex(string name, IReadOnlyList<string> values)
    {
        var chunkCount = (values.Count + IndexChunkSize - 1) / IndexChunkSize;
        SetJson(IndexMetaKey(name), new IndexMeta(values.Count, chunkCount));
        for (var index = 0; index < chunkCount; index++)
            SetJson(IndexChunkKey(name, index), values.Skip(index * IndexChunkSize).Take(IndexChunkSize).ToArray());
    }

    private long ReadLong(string key) =>
        TryReadJson<long>(key, out var value) ? value : 0;

    private T ReadRequiredJson<T>(string key) =>
        TryReadJson<T>(key, out var value)
            ? value
            : throw new InvalidOperationException($"Virtual PLC state '{key}' is missing.");

    private bool TryReadJson<T>(string key, out T value)
    {
        if (_state.TryGet(key, out var element))
        {
            value = element.Deserialize<T>()!;
            return value is not null;
        }
        value = default!;
        return false;
    }

    private void SetJson<T>(string key, T value) =>
        _state.Set(key, JsonSerializer.SerializeToElement(value));

    private static VirtualPlcBlockSnapshot ToBlockSnapshot(BlockMeta meta, byte[] data) =>
        new(meta.BlockKey, meta.PlcName, meta.DbNumber, meta.Size, ComputeSha256(data), data.ToArray());

    private static VirtualPlcFaultSnapshot ToFaultSnapshot(FaultStorage fault, long offset) =>
        new(
            fault.Id,
            fault.Kind,
            fault.Target,
            fault.StartMilliseconds,
            fault.EndMilliseconds,
            fault.Offset,
            fault.Length,
            fault.BitIndex,
            fault.JitterMinimum,
            fault.JitterMaximum,
            fault.ReplacementBytes?.ToArray(),
            fault.FrozenBytes?.ToArray(),
            fault.Enabled,
            IsActive(fault, offset));

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly string EmptyHash = ComputeSha256([]);

    private static (string BlockKey, string PlcName, int DbNumber) ParseBlockKey(string blockKey)
    {
        if (!TryParseBlockKey(blockKey, out var parsed))
            throw new InvalidOperationException("Virtual PLC block key must use the form PLC_NAME.DB<number>.");
        return parsed;
    }

    private static bool TryParseBlockKey(
        string? blockKey,
        out (string BlockKey, string PlcName, int DbNumber) parsed)
    {
        var value = blockKey?.Trim() ?? string.Empty;
        var match = BlockKeyRegex().Match(value);
        if (!match.Success || !int.TryParse(match.Groups["db"].Value, out var dbNumber))
        {
            parsed = default;
            return false;
        }
        var plc = match.Groups["plc"].Value;
        parsed = ($"{plc}.DB{dbNumber}", plc, dbNumber);
        return true;
    }

    private static string NormalizePlcName(string? plcName)
    {
        var value = plcName?.Trim() ?? string.Empty;
        if (!PlcNameRegex().IsMatch(value))
            throw new InvalidOperationException("Virtual PLC name contains unsupported characters.");
        return value;
    }

    private static string NormalizeTarget(string? target) =>
        TryParseBlockKey(target, out var parsed)
            ? parsed.BlockKey
            : NormalizePlcName(target);

    private static void ValidateFaultId(string? faultId)
    {
        if (string.IsNullOrWhiteSpace(faultId) || !FaultIdRegex().IsMatch(faultId))
            throw new InvalidOperationException("Virtual PLC fault id contains unsupported characters.");
    }

    private static string BlockMetaKey(string blockKey) => $"__vplc.block.{blockKey}.metadata";
    private static string BlockDataPrefix(string blockKey) => $"__vplc.block.{blockKey}.data";
    private static string ConnectionKey(string plcName) => $"__vplc.connection.{plcName}";
    private static string FaultKey(string faultId) => $"__vplc.fault.{faultId}";
    private static string AuditSlotKey(long slot) => $"__vplc.audit.slot.{slot:D6}";
    private static string IndexMetaKey(string name) => $"__vplc.index.{name}.meta";
    private static string IndexChunkKey(string name, int index) => $"__vplc.index.{name}.chunk.{index:D4}";

    private sealed record BlockMeta(string BlockKey, string PlcName, int DbNumber, int Size);
    private sealed record ByteStorageMeta(int Length, int ChunkCount);
    private sealed record IndexMeta(int Count, int ChunkCount);
    private sealed record FaultStorage(
        string Id,
        VirtualPlcFaultKind Kind,
        string Target,
        long StartMilliseconds,
        long? EndMilliseconds,
        int Offset,
        int Length,
        int BitIndex,
        int JitterMinimum,
        int JitterMaximum,
        byte[]? ReplacementBytes,
        byte[]? FrozenBytes,
        bool Enabled);
}
