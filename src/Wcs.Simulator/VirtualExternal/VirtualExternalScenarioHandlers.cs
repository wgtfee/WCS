namespace Wcs.Simulator.VirtualExternal;

using System.Text.Json;
using Wcs.Simulator.ScenarioEngine;

public static class VirtualExternalScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(VirtualExternalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new DefineEndpointActionHandler(options),
            new ApplyFaultActionHandler(options),
            new ClearFaultActionHandler(options),
            new InvokeRequestActionHandler(options),
            new ResetCircuitActionHandler(options)
        ];
    }

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(VirtualExternalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new RequestStateAssertionHandler(options),
            new RequestAttemptsAssertionHandler(options),
            new CircuitStateAssertionHandler(options),
            new FaultActiveAssertionHandler(options)
        ];
    }

    private abstract class HandlerBase(VirtualExternalOptions options)
    {
        protected VirtualExternalOptions Options { get; } = options;

        protected VirtualExternalRuntime Runtime(SimulationActionContext context) => new(context.State, Options);
        protected VirtualExternalRuntime Runtime(SimulationAssertionContext context) => new(context.State, Options);

        protected static T ReadPayload<T>(JsonElement element, string kind)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{kind} requires an object payload.");
            return element.Deserialize<T>()
                ?? throw new InvalidOperationException($"{kind} payload is empty.");
        }

        protected static SimulationAssertionOutcome Assertion(
            SimulationAssertionDefinition definition,
            bool passed,
            string expected,
            string actual,
            string message) =>
            new(definition.Id, passed, definition.Kind, definition.Target,
                expected, actual, message, definition.AtMilliseconds);
    }

    private sealed class DefineEndpointActionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "external.endpoint.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineEndpointPayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualExternalSystemKind>(payload.Kind, true, out var kind))
                throw new InvalidOperationException($"Unsupported virtual external system kind '{payload.Kind}'.");
            var result = Runtime(context).DefineEndpoint(
                new VirtualExternalEndpointDefinition(context.Definition.Target, kind),
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ApplyFaultActionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "external.fault.apply";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ApplyFaultPayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualExternalFaultKind>(payload.Kind, true, out var kind))
                throw new InvalidOperationException($"Unsupported virtual external fault kind '{payload.Kind}'.");
            var result = Runtime(context).ApplyFault(
                new VirtualExternalFaultDefinition(
                    context.Definition.Target,
                    payload.EndpointId,
                    kind,
                    payload.StartsAtOffsetMilliseconds,
                    payload.EndsAtOffsetMilliseconds,
                    payload.HttpStatusCode,
                    payload.DelayMilliseconds,
                    payload.ErrorCode),
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ClearFaultActionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "external.fault.clear";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cleared = Runtime(context).ClearFault(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new { cleared })));
        }
    }

    private sealed class InvokeRequestActionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "external.request.invoke";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<InvokePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).Invoke(
                new VirtualExternalInvokeRequest(
                    context.Definition.Target,
                    payload.Operation,
                    payload.IdempotencyKey,
                    payload.PayloadHash,
                    payload.MaxAttempts,
                    payload.TimeoutMilliseconds,
                    payload.RetryDelayMilliseconds),
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ResetCircuitActionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "external.circuit.reset";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Runtime(context).ResetCircuit(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class RequestStateAssertionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "external.request.state";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("external.request.state Expected must be a request state string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetRequest(context.Definition.Target).State.ToString();
            var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Virtual external request state matched." : "Virtual external request state did not match."));
        }
    }

    private sealed class RequestAttemptsAssertionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "external.request.attempts";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.Number ||
                !context.Definition.Expected.TryGetInt32(out var expected))
                throw new InvalidOperationException("external.request.attempts Expected must be an integer.");
            var actual = Runtime(context).GetRequest(context.Definition.Target).Attempts.Count;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual external request attempt count matched." : "Virtual external request attempt count did not match."));
        }
    }

    private sealed class CircuitStateAssertionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "external.circuit.state";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("external.circuit.state Expected must be a circuit state string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetEndpoint(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds).CircuitState.ToString();
            var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Virtual external circuit state matched." : "Virtual external circuit state did not match."));
        }
    }

    private sealed class FaultActiveAssertionHandler(VirtualExternalOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "external.fault.active";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("external.fault.active Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).ListFaults(true, context.Clock.CurrentOffsetMilliseconds)
                .Any(item => string.Equals(item.FaultId, context.Definition.Target, StringComparison.Ordinal));
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual external fault activity matched." : "Virtual external fault activity did not match."));
        }
    }

    private sealed class DefineEndpointPayload
    {
        public string Kind { get; set; } = nameof(VirtualExternalSystemKind.Custom);
    }

    private sealed class ApplyFaultPayload
    {
        public string EndpointId { get; set; } = string.Empty;
        public string Kind { get; set; } = nameof(VirtualExternalFaultKind.Unavailable);
        public long StartsAtOffsetMilliseconds { get; set; }
        public long EndsAtOffsetMilliseconds { get; set; }
        public int? HttpStatusCode { get; set; }
        public long DelayMilliseconds { get; set; }
        public string? ErrorCode { get; set; }
    }

    private sealed class InvokePayload
    {
        public string Operation { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public int MaxAttempts { get; set; } = 1;
        public long? TimeoutMilliseconds { get; set; }
        public long RetryDelayMilliseconds { get; set; }
    }
}