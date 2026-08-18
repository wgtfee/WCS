namespace Wcs.Simulator.VirtualPlc;

using System.Text.Json;
using Wcs.Simulator.ScenarioEngine;

public static class VirtualPlcScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(VirtualPlcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new DefineBlockActionHandler(options),
            new WriteBlockActionHandler(options),
            new ReadBlockActionHandler(options),
            new SetConnectionActionHandler(options),
            new ApplyFaultActionHandler(options),
            new ClearFaultActionHandler(options)
        ];
    }

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(VirtualPlcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new BlockEqualsAssertionHandler(options),
            new ConnectionAssertionHandler(options),
            new FaultActiveAssertionHandler(options)
        ];
    }

    private abstract class HandlerBase(VirtualPlcOptions options)
    {
        protected VirtualPlcOptions Options { get; } = options;

        protected VirtualPlcRuntime Runtime(SimulationActionContext context) =>
            new(context.State, Options, context.Random.CaptureState());

        protected VirtualPlcRuntime Runtime(SimulationAssertionContext context) =>
            new(context.State, Options);

        protected static T ReadPayload<T>(JsonElement element, string actionKind)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{actionKind} requires an object payload.");
            return element.Deserialize<T>()
                ?? throw new InvalidOperationException($"{actionKind} payload is empty.");
        }

        protected static void StoreResult(
            SimulationStateStore state,
            string? stateKey,
            VirtualPlcOperationResult result)
        {
            if (!string.IsNullOrWhiteSpace(stateKey))
                state.Set(stateKey, JsonSerializer.SerializeToElement(result));
        }

        protected static SimulationAssertionOutcome Assertion(
            SimulationAssertionDefinition definition,
            bool passed,
            string expected,
            string actual,
            string message) =>
            new(
                definition.Id,
                passed,
                definition.Kind,
                definition.Target,
                expected,
                actual,
                message,
                definition.AtMilliseconds);
    }

    private sealed class DefineBlockActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.block.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineBlockPayload>(context.Definition.Payload, Kind);
            byte[] initial = [];
            if (!string.IsNullOrWhiteSpace(payload.InitialBase64))
            {
                try { initial = Convert.FromBase64String(payload.InitialBase64); }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException("plc.block.define InitialBase64 is invalid.", exception);
                }
            }

            var snapshot = Runtime(context).DefineBlock(
                context.Definition.Target,
                payload.Size,
                initial,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
            {
                kind = Kind,
                snapshot.BlockKey,
                snapshot.Size,
                snapshot.Sha256
            })));
        }
    }

    private sealed class WriteBlockActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.block.write";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<WriteBlockPayload>(context.Definition.Payload, Kind);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(payload.DataBase64 ?? string.Empty); }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("plc.block.write DataBase64 is invalid.", exception);
            }
            if (bytes.Length > Options.MaximumScenarioTransferBytes)
                throw new InvalidOperationException("plc.block.write exceeds MaximumScenarioTransferBytes.");

            var result = Runtime(context).Write(
                context.Definition.Target,
                payload.Offset,
                bytes,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            StoreResult(context.State, payload.ResultStateKey, result);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ReadBlockActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.block.read";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ReadBlockPayload>(context.Definition.Payload, Kind);
            if (payload.Count < 1 || payload.Count > Options.MaximumScenarioTransferBytes)
                throw new InvalidOperationException("plc.block.read Count is outside MaximumScenarioTransferBytes.");

            var result = Runtime(context).Read(
                context.Definition.Target,
                payload.Offset,
                payload.Count,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            StoreResult(context.State, payload.ResultStateKey, result);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class SetConnectionActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.connection.set";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<SetConnectionPayload>(context.Definition.Payload, Kind);
            Runtime(context).SetConnection(
                context.Definition.Target,
                payload.Connected,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
            {
                kind = Kind,
                plc = context.Definition.Target,
                payload.Connected
            })));
        }
    }

    private sealed class ApplyFaultActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.fault.apply";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ApplyFaultPayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualPlcFaultKind>(payload.Kind, ignoreCase: true, out var faultKind))
                throw new InvalidOperationException($"Unsupported virtual PLC fault kind '{payload.Kind}'.");

            byte[]? replacement = null;
            if (!string.IsNullOrWhiteSpace(payload.ReplacementBase64))
            {
                try { replacement = Convert.FromBase64String(payload.ReplacementBase64); }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException("plc.fault.apply ReplacementBase64 is invalid.", exception);
                }
            }

            var snapshot = Runtime(context).ApplyFault(
                new VirtualPlcFaultDefinition
                {
                    Id = payload.Id,
                    Kind = faultKind,
                    Target = context.Definition.Target,
                    StartMilliseconds = payload.StartMilliseconds ?? context.Clock.CurrentOffsetMilliseconds,
                    EndMilliseconds = payload.EndMilliseconds,
                    Offset = payload.Offset,
                    Length = payload.Length,
                    BitIndex = payload.BitIndex,
                    JitterMinimum = payload.JitterMinimum,
                    JitterMaximum = payload.JitterMaximum,
                    ReplacementBytes = replacement
                },
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class ClearFaultActionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "plc.fault.clear";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Runtime(context).ClearFault(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class BlockEqualsAssertionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "plc.block.equals";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = ReadPayload<BlockEqualsExpected>(context.Definition.Expected, Kind);
            byte[] expectedBytes;
            try { expectedBytes = Convert.FromBase64String(expected.DataBase64 ?? string.Empty); }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("plc.block.equals DataBase64 is invalid.", exception);
            }
            if (expectedBytes.Length > Options.MaximumScenarioTransferBytes)
                throw new InvalidOperationException("plc.block.equals exceeds MaximumScenarioTransferBytes.");

            var block = Runtime(context).GetBlock(context.Definition.Target);
            if (expected.Offset < 0 || checked(expected.Offset + expectedBytes.Length) > block.Size)
                throw new InvalidOperationException("plc.block.equals range exceeds the target block.");
            var actualBytes = block.Data.AsSpan(expected.Offset, expectedBytes.Length).ToArray();
            var passed = actualBytes.AsSpan().SequenceEqual(expectedBytes);
            return ValueTask.FromResult(Assertion(
                context.Definition,
                passed,
                Convert.ToBase64String(expectedBytes),
                Convert.ToBase64String(actualBytes),
                passed ? "Virtual PLC block bytes matched." : "Virtual PLC block bytes did not match."));
        }
    }

    private sealed class ConnectionAssertionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "plc.connected";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("plc.connected Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).IsConnected(context.Definition.Target, context.Clock.CurrentOffsetMilliseconds);
            return ValueTask.FromResult(Assertion(
                context.Definition,
                expected == actual,
                expected.ToString(),
                actual.ToString(),
                expected == actual ? "Virtual PLC connection state matched." : "Virtual PLC connection state did not match."));
        }
    }

    private sealed class FaultActiveAssertionHandler(VirtualPlcOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "plc.fault.active";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("plc.fault.active Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).IsFaultActive(context.Definition.Target, context.Clock.CurrentOffsetMilliseconds);
            return ValueTask.FromResult(Assertion(
                context.Definition,
                expected == actual,
                expected.ToString(),
                actual.ToString(),
                expected == actual ? "Virtual PLC fault activity matched." : "Virtual PLC fault activity did not match."));
        }
    }

    private sealed class DefineBlockPayload
    {
        public int Size { get; set; }
        public string? InitialBase64 { get; set; }
    }

    private sealed class WriteBlockPayload
    {
        public int Offset { get; set; }
        public string? DataBase64 { get; set; }
        public string? ResultStateKey { get; set; }
    }

    private sealed class ReadBlockPayload
    {
        public int Offset { get; set; }
        public int Count { get; set; }
        public string? ResultStateKey { get; set; }
    }

    private sealed class SetConnectionPayload
    {
        public bool Connected { get; set; }
    }

    private sealed class ApplyFaultPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long? StartMilliseconds { get; set; }
        public long? EndMilliseconds { get; set; }
        public int Offset { get; set; }
        public int Length { get; set; } = 1;
        public int BitIndex { get; set; }
        public int JitterMinimum { get; set; } = -1;
        public int JitterMaximum { get; set; } = 1;
        public string? ReplacementBase64 { get; set; }
    }

    private sealed class BlockEqualsExpected
    {
        public int Offset { get; set; }
        public string? DataBase64 { get; set; }
    }
}
