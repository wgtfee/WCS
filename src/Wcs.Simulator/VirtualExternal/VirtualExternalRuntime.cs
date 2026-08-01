namespace Wcs.Simulator.VirtualExternal;

using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Simulator.ScenarioEngine;

/// <summary>
/// Deterministic, process-local model of external MES/SQL/network dependencies.
/// It never opens sockets, connects to SQL Server, or invokes production clients.
/// Every endpoint, fault, retry, circuit and idempotency record is stored in the
/// S1 SimulationStateStore so Checkpoint/Replay and FinalStateHash cover S5 state.
/// </summary>
public sealed partial class VirtualExternalRuntime
{
    private const int IndexChunkSize = 16;
    private const string EndpointIndexName = "endpoints";
    private const string FaultIndexName = "faults";
    private const string RequestIndexName = "requests";
    private const string OperationSequenceKey = "__vexternal.operationSequence";
    private const string RequestSequenceKey = "__vexternal.requestSequence";
    private const string AuditCountKey = "__vexternal.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualExternalOptions _options;

    public VirtualExternalRuntime(SimulationStateStore state, VirtualExternalOptions options)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public VirtualExternalEndpointSnapshot DefineEndpoint(
        VirtualExternalEndpointDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var endpointId = NormalizeId(definition.EndpointId, nameof(definition.EndpointId));
        if (_state.Contains(EndpointKey(endpointId)))
            throw new InvalidOperationException($"Virtual external endpoint '{endpointId}' is already defined.");

        var ids = ReadIndex(EndpointIndexName).ToList();
        if (ids.Count >= _options.MaximumEndpoints)
            throw new InvalidOperationException("Virtual external runtime has reached MaximumEndpoints.");

        var stored = new EndpointStorage(endpointId, definition.Kind,
            VirtualExternalCircuitState.Closed, 0, null, 1);
        SetJson(EndpointKey(endpointId), stored);
        ids.Add(endpointId);
        WriteIndex(EndpointIndexName, ids.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "endpoint.define", endpointId,
            $"kind={definition.Kind}", true);
        return ToSnapshot(stored);
    }

    public VirtualExternalFaultSnapshot ApplyFault(
        VirtualExternalFaultDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var faultId = NormalizeId(definition.FaultId, nameof(definition.FaultId));
        var endpointId = NormalizeId(definition.EndpointId, nameof(definition.EndpointId));
        _ = ReadRequiredEndpoint(endpointId);
        if (_state.Contains(FaultKey(faultId)))
            throw new InvalidOperationException($"Virtual external fault '{faultId}' is already defined.");
        if (definition.StartsAtOffsetMilliseconds < 0 ||
            definition.EndsAtOffsetMilliseconds <= definition.StartsAtOffsetMilliseconds)
            throw new InvalidOperationException("Virtual external fault window is invalid.");
        if (definition.DelayMilliseconds < 0 || definition.DelayMilliseconds > _options.MaximumDelayMilliseconds)
            throw new InvalidOperationException("Virtual external fault delay is outside MaximumDelayMilliseconds.");
        if (definition.Kind == VirtualExternalFaultKind.HttpStatus &&
            definition.HttpStatusCode is not (>= 100 and <= 599))
            throw new InvalidOperationException("HttpStatus faults require a status code between 100 and 599.");
        if (definition.Kind != VirtualExternalFaultKind.HttpStatus && definition.HttpStatusCode is not null)
            throw new InvalidOperationException("HttpStatusCode is only valid for HttpStatus faults.");

        var overlapping = ListFaults(false, virtualOffsetMilliseconds)
            .Any(item => !item.Cleared &&
                         string.Equals(item.EndpointId, endpointId, StringComparison.Ordinal) &&
                         WindowsOverlap(item.StartsAtOffsetMilliseconds, item.EndsAtOffsetMilliseconds,
                             definition.StartsAtOffsetMilliseconds, definition.EndsAtOffsetMilliseconds));
        if (overlapping)
            throw new InvalidOperationException("Virtual external fault windows for one endpoint may not overlap in S5.");

        var ids = ReadIndex(FaultIndexName).ToList();
        if (ids.Count >= _options.MaximumFaults)
            throw new InvalidOperationException("Virtual external runtime has reached MaximumFaults.");

        var stored = new FaultStorage(faultId, endpointId, definition.Kind,
            definition.StartsAtOffsetMilliseconds, definition.EndsAtOffsetMilliseconds,
            definition.HttpStatusCode, definition.DelayMilliseconds,
            NormalizeOptionalCode(definition.ErrorCode), false, 1);
        SetJson(FaultKey(faultId), stored);
        ids.Add(faultId);
        WriteIndex(FaultIndexName, ids.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "fault.apply", faultId,
            $"endpoint={endpointId};kind={definition.Kind};window={definition.StartsAtOffsetMilliseconds}-{definition.EndsAtOffsetMilliseconds}", true);
        return ToSnapshot(stored);
    }

    public bool ClearFault(
        string faultId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        faultId = NormalizeId(faultId, nameof(faultId));
        if (!TryReadJson<FaultStorage>(FaultKey(faultId), out var fault) || fault.Cleared)
        {
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "fault.clear", faultId, "missing-or-cleared", false);
            return false;
        }

        SetJson(FaultKey(faultId), fault with { Cleared = true, Version = fault.Version + 1 });
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "fault.clear", faultId,
            $"endpoint={fault.EndpointId}", true);
        return true;
    }

    public VirtualExternalRequestSnapshot Invoke(
        VirtualExternalInvokeRequest request,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpointId = NormalizeId(request.EndpointId, nameof(request.EndpointId));
        var operation = NormalizeId(request.Operation, nameof(request.Operation));
        var idempotencyKey = NormalizeId(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var payloadHash = NormalizeHash(request.PayloadHash);
        if (request.MaxAttempts < 1 || request.MaxAttempts > _options.MaximumRetryAttempts)
            throw new InvalidOperationException("Virtual external MaxAttempts is outside MaximumRetryAttempts.");
        var timeout = request.TimeoutMilliseconds ?? _options.DefaultTimeoutMilliseconds;
        if (timeout < 1 || timeout > _options.MaximumDelayMilliseconds)
            throw new InvalidOperationException("Virtual external timeout is outside MaximumDelayMilliseconds.");
        if (request.RetryDelayMilliseconds < 0 || request.RetryDelayMilliseconds > _options.MaximumDelayMilliseconds)
            throw new InvalidOperationException("Virtual external retry delay is outside MaximumDelayMilliseconds.");

        _ = ReadRequiredEndpoint(endpointId);
        if (TryReadJson<string>(IdempotencyKey(endpointId, idempotencyKey), out var priorRequestId) &&
            TryReadJson<RequestStorage>(RequestKey(priorRequestId), out var prior) &&
            prior.State == VirtualExternalRequestState.Succeeded)
        {
            if (!string.Equals(prior.Operation, operation, StringComparison.Ordinal) ||
                !string.Equals(prior.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Virtual external idempotency key was reused with different operation or payload hash.");
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "request.idempotent-replay", prior.RequestId,
                $"endpoint={endpointId};key={idempotencyKey}", true);
            return ToSnapshot(prior) with { IdempotencyReplayed = true };
        }

        var requestIds = ReadIndex(RequestIndexName).ToList();
        if (requestIds.Count >= _options.MaximumRequests)
            throw new InvalidOperationException("Virtual external runtime has reached MaximumRequests.");

        var sequence = _state.Increment(RequestSequenceKey, 1);
        var requestId = $"EXTREQ-{sequence:D12}";
        var attempts = new List<VirtualExternalAttemptSnapshot>();
        var endpoint = ReadRequiredEndpoint(endpointId);
        VirtualExternalRequestState finalState = VirtualExternalRequestState.Failed;
        long completedAt = virtualOffsetMilliseconds;

        for (var attempt = 1; attempt <= request.MaxAttempts; attempt++)
        {
            var attemptOffset = checked(virtualOffsetMilliseconds + checked((attempt - 1L) * request.RetryDelayMilliseconds));
            endpoint = RefreshCircuitForOffset(endpoint, attemptOffset);
            if (endpoint.CircuitState == VirtualExternalCircuitState.Open)
            {
                attempts.Add(new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    VirtualExternalRequestState.RejectedByCircuit, 0, null, "CIRCUIT_OPEN", false, null));
                finalState = VirtualExternalRequestState.RejectedByCircuit;
                completedAt = attemptOffset;
                break;
            }

            var fault = FindActiveFault(endpointId, attemptOffset);
            var result = EvaluateAttempt(attempt, attemptOffset, timeout, fault);
            attempts.Add(result);
            completedAt = checked(attemptOffset + result.DurationMilliseconds);
            finalState = result.State;

            if (result.State == VirtualExternalRequestState.Succeeded)
            {
                endpoint = RecordSuccess(endpoint);
                SetJson(EndpointKey(endpointId), endpoint);
                break;
            }

            endpoint = RecordFailure(endpoint, attemptOffset);
            SetJson(EndpointKey(endpointId), endpoint);
        }

        var stored = new RequestStorage(requestId, endpointId, operation, idempotencyKey, payloadHash,
            finalState, attempts.ToArray(), false, virtualOffsetMilliseconds, completedAt, 1);
        SetJson(RequestKey(requestId), stored);
        requestIds.Add(requestId);
        WriteIndex(RequestIndexName, requestIds);
        if (finalState == VirtualExternalRequestState.Succeeded)
            SetJson(IdempotencyKey(endpointId, idempotencyKey), requestId);

        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "request.invoke", requestId,
            $"endpoint={endpointId};operation={operation};attempts={attempts.Count};state={finalState}",
            finalState == VirtualExternalRequestState.Succeeded);
        return ToSnapshot(stored);
    }

    public VirtualExternalEndpointSnapshot ResetCircuit(
        string endpointId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var endpoint = ReadRequiredEndpoint(NormalizeId(endpointId, nameof(endpointId)));
        var updated = endpoint with
        {
            CircuitState = VirtualExternalCircuitState.Closed,
            ConsecutiveFailures = 0,
            CircuitOpenUntilOffsetMilliseconds = null,
            Version = endpoint.Version + 1
        };
        SetJson(EndpointKey(updated.EndpointId), updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "circuit.reset", updated.EndpointId, null, true);
        return ToSnapshot(updated);
    }

    public VirtualExternalEndpointSnapshot GetEndpoint(string endpointId, long virtualOffsetMilliseconds = 0)
    {
        var endpoint = ReadRequiredEndpoint(NormalizeId(endpointId, nameof(endpointId)));
        endpoint = RefreshCircuitForOffset(endpoint, virtualOffsetMilliseconds);
        SetJson(EndpointKey(endpoint.EndpointId), endpoint);
        return ToSnapshot(endpoint);
    }

    public IReadOnlyList<VirtualExternalEndpointSnapshot> ListEndpoints(long virtualOffsetMilliseconds = 0) =>
        ReadIndex(EndpointIndexName)
            .Select(id => GetEndpoint(id, virtualOffsetMilliseconds))
            .OrderBy(static item => item.EndpointId, StringComparer.Ordinal)
            .ToArray();

    public VirtualExternalFaultSnapshot GetFault(string faultId) =>
        ToSnapshot(ReadRequiredFault(NormalizeId(faultId, nameof(faultId))));

    public IReadOnlyList<VirtualExternalFaultSnapshot> ListFaults(
        bool activeOnly = false,
        long virtualOffsetMilliseconds = 0) =>
        ReadIndex(FaultIndexName)
            .Select(ReadRequiredFault)
            .Where(item => !activeOnly || IsActive(item, virtualOffsetMilliseconds))
            .Select(ToSnapshot)
            .OrderBy(static item => item.FaultId, StringComparer.Ordinal)
            .ToArray();

    public VirtualExternalRequestSnapshot GetRequest(string requestId) =>
        ToSnapshot(ReadRequiredRequest(NormalizeId(requestId, nameof(requestId))));

    public IReadOnlyList<VirtualExternalRequestSnapshot> ListRequests() =>
        ReadIndex(RequestIndexName)
            .Select(ReadRequiredRequest)
            .Select(ToSnapshot)
            .OrderBy(static item => item.RequestId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<VirtualExternalAuditRecord> ListAudit()
    {
        var total = Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords);
        if (total <= 0)
            return [];
        var sequence = ReadInt64(OperationSequenceKey);
        var first = Math.Max(1, sequence - total + 1);
        var result = new List<VirtualExternalAuditRecord>((int)total);
        for (var current = first; current <= sequence; current++)
        {
            var slot = (int)((current - 1) % _options.MaximumAuditRecords);
            if (TryReadJson<VirtualExternalAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == current)
                result.Add(record);
        }
        return result;
    }

    public VirtualExternalStatus GetStatus(long virtualOffsetMilliseconds = 0)
    {
        var endpoints = ListEndpoints(virtualOffsetMilliseconds);
        return new VirtualExternalStatus(
            endpoints.Count,
            ListFaults(true, virtualOffsetMilliseconds).Count,
            ReadIndex(RequestIndexName).Count,
            endpoints.Count(static item => item.CircuitState == VirtualExternalCircuitState.Open),
            (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords),
            ReadInt64(OperationSequenceKey));
    }

    private VirtualExternalAttemptSnapshot EvaluateAttempt(
        int attempt,
        long attemptOffset,
        long timeoutMilliseconds,
        FaultStorage? fault)
    {
        if (fault is null)
            return new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                VirtualExternalRequestState.Succeeded, 0, 200, null, false, null);

        return fault.Kind switch
        {
            VirtualExternalFaultKind.HighLatency when fault.DelayMilliseconds <= timeoutMilliseconds =>
                new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    VirtualExternalRequestState.Succeeded, fault.DelayMilliseconds, 200, null, false, fault.FaultId),
            VirtualExternalFaultKind.DuplicateResponse =>
                new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    VirtualExternalRequestState.Succeeded, fault.DelayMilliseconds, 200, null, true, fault.FaultId),
            VirtualExternalFaultKind.Timeout or VirtualExternalFaultKind.SqlCommandTimeout =>
                new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    VirtualExternalRequestState.TimedOut, timeoutMilliseconds, null,
                    fault.ErrorCode ?? (fault.Kind == VirtualExternalFaultKind.SqlCommandTimeout ? "SQL_COMMAND_TIMEOUT" : "TIMEOUT"),
                    false, fault.FaultId),
            VirtualExternalFaultKind.HighLatency =>
                new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    VirtualExternalRequestState.TimedOut, timeoutMilliseconds, null, "HIGH_LATENCY_TIMEOUT", false, fault.FaultId),
            VirtualExternalFaultKind.HttpStatus =>
                new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                    fault.HttpStatusCode is >= 200 and <= 399
                        ? VirtualExternalRequestState.Succeeded
                        : VirtualExternalRequestState.Failed,
                    fault.DelayMilliseconds, fault.HttpStatusCode, fault.ErrorCode ?? $"HTTP_{fault.HttpStatusCode}", false, fault.FaultId),
            _ => new VirtualExternalAttemptSnapshot(attempt, attemptOffset,
                VirtualExternalRequestState.Failed, fault.DelayMilliseconds, null,
                fault.ErrorCode ?? DefaultErrorCode(fault.Kind), false, fault.FaultId)
        };
    }

    private EndpointStorage RefreshCircuitForOffset(EndpointStorage endpoint, long virtualOffsetMilliseconds)
    {
        if (endpoint.CircuitState != VirtualExternalCircuitState.Open ||
            endpoint.CircuitOpenUntilOffsetMilliseconds is null ||
            virtualOffsetMilliseconds < endpoint.CircuitOpenUntilOffsetMilliseconds.Value)
            return endpoint;

        return endpoint with
        {
            CircuitState = VirtualExternalCircuitState.HalfOpen,
            Version = endpoint.Version + 1
        };
    }

    private EndpointStorage RecordSuccess(EndpointStorage endpoint) => endpoint with
    {
        CircuitState = VirtualExternalCircuitState.Closed,
        ConsecutiveFailures = 0,
        CircuitOpenUntilOffsetMilliseconds = null,
        Version = endpoint.Version + 1
    };

    private EndpointStorage RecordFailure(EndpointStorage endpoint, long virtualOffsetMilliseconds)
    {
        var failures = endpoint.ConsecutiveFailures + 1;
        var shouldOpen = endpoint.CircuitState == VirtualExternalCircuitState.HalfOpen ||
                         failures >= _options.CircuitFailureThreshold;
        return endpoint with
        {
            CircuitState = shouldOpen ? VirtualExternalCircuitState.Open : VirtualExternalCircuitState.Closed,
            ConsecutiveFailures = failures,
            CircuitOpenUntilOffsetMilliseconds = shouldOpen
                ? checked(virtualOffsetMilliseconds + _options.CircuitOpenMilliseconds)
                : null,
            Version = endpoint.Version + 1
        };
    }

    private FaultStorage? FindActiveFault(string endpointId, long virtualOffsetMilliseconds) =>
        ReadIndex(FaultIndexName)
            .Select(ReadRequiredFault)
            .Where(item => string.Equals(item.EndpointId, endpointId, StringComparison.Ordinal) &&
                           IsActive(item, virtualOffsetMilliseconds))
            .OrderBy(static item => item.FaultId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsActive(FaultStorage fault, long virtualOffsetMilliseconds) =>
        !fault.Cleared &&
        virtualOffsetMilliseconds >= fault.StartsAtOffsetMilliseconds &&
        virtualOffsetMilliseconds < fault.EndsAtOffsetMilliseconds;

    private EndpointStorage ReadRequiredEndpoint(string endpointId) =>
        TryReadJson<EndpointStorage>(EndpointKey(endpointId), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual external endpoint '{endpointId}' was not found.");

    private FaultStorage ReadRequiredFault(string faultId) =>
        TryReadJson<FaultStorage>(FaultKey(faultId), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual external fault '{faultId}' was not found.");

    private RequestStorage ReadRequiredRequest(string requestId) =>
        TryReadJson<RequestStorage>(RequestKey(requestId), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual external request '{requestId}' was not found.");

    private void AppendAudit(
        DateTimeOffset occurredAtUtc,
        long virtualOffsetMilliseconds,
        string operation,
        string target,
        string? detail,
        bool success)
    {
        var sequence = _state.Increment(OperationSequenceKey, 1);
        var slot = (int)((sequence - 1) % _options.MaximumAuditRecords);
        SetJson(AuditSlotKey(slot), new VirtualExternalAuditRecord(sequence, occurredAtUtc,
            virtualOffsetMilliseconds, operation, target, detail, success));
        _state.Increment(AuditCountKey, 1);
    }

    private IReadOnlyList<string> ReadIndex(string name)
    {
        var count = (int)ReadInt64(IndexCountKey(name));
        if (count == 0)
            return [];
        var result = new List<string>(count);
        var chunks = (count + IndexChunkSize - 1) / IndexChunkSize;
        for (var index = 0; index < chunks; index++)
        {
            if (TryReadJson<string[]>(IndexChunkKey(name, index), out var values))
                result.AddRange(values);
        }
        if (result.Count != count)
            throw new InvalidOperationException($"Virtual external index '{name}' is inconsistent.");
        return result;
    }

    private void WriteIndex(string name, IReadOnlyList<string> values)
    {
        SetJson(IndexCountKey(name), values.Count);
        for (var offset = 0; offset < values.Count; offset += IndexChunkSize)
            SetJson(IndexChunkKey(name, offset / IndexChunkSize), values.Skip(offset).Take(IndexChunkSize).ToArray());
    }

    private long ReadInt64(string key)
    {
        if (!_state.TryGet(key, out var value))
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidOperationException($"Virtual external counter '{key}' is invalid.");
        return number;
    }

    private void SetJson<T>(string key, T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        _state.Set(key, document.RootElement);
    }

    private bool TryReadJson<T>(string key, out T value)
    {
        if (_state.TryGet(key, out var element))
        {
            var parsed = element.Deserialize<T>();
            if (parsed is not null)
            {
                value = parsed;
                return true;
            }
        }
        value = default!;
        return false;
    }

    private static string NormalizeId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
            throw new InvalidOperationException($"Virtual external {name} contains unsupported characters.");
        return value;
    }

    private static string NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256Regex().IsMatch(value))
            throw new InvalidOperationException("Virtual external PayloadHash must be a 64-character SHA-256 hex string.");
        return value.ToLowerInvariant();
    }

    private static string? NormalizeOptionalCode(string? value)
    {
        if (value is null)
            return null;
        return NormalizeId(value, nameof(value));
    }

    private static bool WindowsOverlap(long aStart, long aEnd, long bStart, long bEnd) =>
        aStart < bEnd && bStart < aEnd;

    private static string DefaultErrorCode(VirtualExternalFaultKind kind) => kind switch
    {
        VirtualExternalFaultKind.Unavailable => "UNAVAILABLE",
        VirtualExternalFaultKind.InvalidResponse => "INVALID_RESPONSE",
        VirtualExternalFaultKind.SqlDeadlock => "SQL_DEADLOCK",
        VirtualExternalFaultKind.ConnectionReset => "CONNECTION_RESET",
        VirtualExternalFaultKind.PacketLoss => "PACKET_LOSS",
        VirtualExternalFaultKind.HalfOpen => "HALF_OPEN_FAILURE",
        _ => kind.ToString().ToUpperInvariant()
    };

    private static string EndpointKey(string id) => $"__vexternal.endpoint.{id}";
    private static string FaultKey(string id) => $"__vexternal.fault.{id}";
    private static string RequestKey(string id) => $"__vexternal.request.{id}";
    private static string IdempotencyKey(string endpointId, string key) => $"__vexternal.idempotency.{endpointId}.{key}";
    private static string AuditSlotKey(int slot) => $"__vexternal.audit.{slot:D6}";
    private static string IndexCountKey(string name) => $"__vexternal.index.{name}.count";
    private static string IndexChunkKey(string name, int index) => $"__vexternal.index.{name}.{index:D6}";

    private static VirtualExternalEndpointSnapshot ToSnapshot(EndpointStorage value) =>
        new(value.EndpointId, value.Kind, value.CircuitState, value.ConsecutiveFailures,
            value.CircuitOpenUntilOffsetMilliseconds, value.Version);

    private static VirtualExternalFaultSnapshot ToSnapshot(FaultStorage value) =>
        new(value.FaultId, value.EndpointId, value.Kind, value.StartsAtOffsetMilliseconds,
            value.EndsAtOffsetMilliseconds, value.HttpStatusCode, value.DelayMilliseconds,
            value.ErrorCode, value.Cleared, value.Version);

    private static VirtualExternalRequestSnapshot ToSnapshot(RequestStorage value) =>
        new(value.RequestId, value.EndpointId, value.Operation, value.IdempotencyKey,
            value.PayloadHash, value.State, value.Attempts, value.IdempotencyReplayed,
            value.StartedAtOffsetMilliseconds, value.CompletedAtOffsetMilliseconds, value.Version);

    private sealed record EndpointStorage(
        string EndpointId,
        VirtualExternalSystemKind Kind,
        VirtualExternalCircuitState CircuitState,
        int ConsecutiveFailures,
        long? CircuitOpenUntilOffsetMilliseconds,
        long Version);

    private sealed record FaultStorage(
        string FaultId,
        string EndpointId,
        VirtualExternalFaultKind Kind,
        long StartsAtOffsetMilliseconds,
        long EndsAtOffsetMilliseconds,
        int? HttpStatusCode,
        long DelayMilliseconds,
        string? ErrorCode,
        bool Cleared,
        long Version);

    private sealed record RequestStorage(
        string RequestId,
        string EndpointId,
        string Operation,
        string IdempotencyKey,
        string PayloadHash,
        VirtualExternalRequestState State,
        IReadOnlyList<VirtualExternalAttemptSnapshot> Attempts,
        bool IdempotencyReplayed,
        long StartedAtOffsetMilliseconds,
        long CompletedAtOffsetMilliseconds,
        long Version);
}