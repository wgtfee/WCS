namespace Wcs.Simulator.VirtualRgv;

using System.Text.Json;
using Wcs.Core.TransportScheduling;
using Wcs.Simulator.ScenarioEngine;

public static class VirtualRgvScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(VirtualRgvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new DefineSegmentActionHandler(options),
            new DefineVehicleActionHandler(options),
            new AssignRouteActionHandler(options),
            new AdvanceVehicleActionHandler(options),
            new SetOnlineActionHandler(options),
            new LoadVehicleActionHandler(options),
            new UnloadVehicleActionHandler(options)
        ];
    }

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(VirtualRgvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new VehicleAtNodeAssertionHandler(options),
            new VehicleOnSegmentAssertionHandler(options),
            new VehicleStateAssertionHandler(options),
            new VehicleLoadAssertionHandler(options),
            new RouteCompletedAssertionHandler(options),
            new SegmentOccupiedByAssertionHandler(options),
            new BatteryAtLeastAssertionHandler(options)
        ];
    }

    private abstract class HandlerBase(VirtualRgvOptions options)
    {
        protected VirtualRgvOptions Options { get; } = options;

        protected VirtualRgvRuntime Runtime(SimulationActionContext context) => new(context.State, Options);
        protected VirtualRgvRuntime Runtime(SimulationAssertionContext context) => new(context.State, Options);

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

    private sealed class DefineSegmentActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.segment.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineSegmentPayload>(context.Definition.Payload, Kind);
            var snapshot = Runtime(context).DefineSegment(
                new VirtualRgvSegmentDefinition
                {
                    SegmentId = context.Definition.Target,
                    FromNodeId = payload.FromNodeId,
                    ToNodeId = payload.ToNodeId,
                    LengthMillimeters = payload.LengthMillimeters,
                    SpeedLimitMillimetersPerSecond = payload.SpeedLimitMillimetersPerSecond,
                    Enabled = payload.Enabled
                },
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class DefineVehicleActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.vehicle.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineVehiclePayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<TransportVehicleCapability>(payload.Capabilities, true, out var capabilities))
                throw new InvalidOperationException($"Unsupported virtual RGV capability '{payload.Capabilities}'.");
            var snapshot = Runtime(context).DefineVehicle(
                new VirtualRgvVehicleDefinition
                {
                    VehicleId = context.Definition.Target,
                    InitialNodeId = payload.InitialNodeId,
                    SpeedMillimetersPerSecond = payload.SpeedMillimetersPerSecond,
                    BatteryPercent = payload.BatteryPercent,
                    IsOnline = payload.IsOnline,
                    LoadId = payload.LoadId,
                    Capabilities = capabilities
                },
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class AssignRouteActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.route.assign";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<AssignRoutePayload>(context.Definition.Payload, Kind);
            var snapshot = Runtime(context).AssignRoute(
                context.Definition.Target,
                payload.SegmentIds ?? [],
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class AdvanceVehicleActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.vehicle.advance";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Runtime(context).AdvanceVehicle(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class SetOnlineActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.vehicle.online.set";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<SetOnlinePayload>(context.Definition.Payload, Kind);
            var snapshot = Runtime(context).SetOnline(
                context.Definition.Target,
                payload.IsOnline,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class LoadVehicleActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.vehicle.load";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<LoadPayload>(context.Definition.Payload, Kind);
            var snapshot = Runtime(context).Load(
                context.Definition.Target,
                payload.LoadId,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class UnloadVehicleActionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "rgv.vehicle.unload";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<UnloadPayload>(context.Definition.Payload, Kind);
            var snapshot = Runtime(context).Unload(
                context.Definition.Target,
                payload.ExpectedLoadId,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class VehicleAtNodeAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.vehicle.at-node";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("rgv.vehicle.at-node Expected must be a node id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var vehicle = Runtime(context).GetVehicle(context.Definition.Target);
            var actual = vehicle.IsAtNode ? vehicle.CurrentNodeId ?? string.Empty : string.Empty;
            var passed = string.Equals(expected, actual, StringComparison.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Virtual RGV node matched." : "Virtual RGV node did not match."));
        }
    }

    private sealed class VehicleOnSegmentAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.vehicle.on-segment";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("rgv.vehicle.on-segment Expected must be a segment id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetVehicle(context.Definition.Target).CurrentSegmentId ?? string.Empty;
            var passed = string.Equals(expected, actual, StringComparison.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Virtual RGV segment matched." : "Virtual RGV segment did not match."));
        }
    }

    private sealed class VehicleStateAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.vehicle.state";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("rgv.vehicle.state Expected must be a state string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            if (!Enum.TryParse<TransportVehicleOperatingState>(expected, true, out var expectedState))
                throw new InvalidOperationException($"Unsupported virtual RGV state '{expected}'.");
            var actualState = Runtime(context).GetVehicle(context.Definition.Target).State;
            var passed = expectedState == actualState;
            return ValueTask.FromResult(Assertion(context.Definition, passed, expectedState.ToString(), actualState.ToString(),
                passed ? "Virtual RGV state matched." : "Virtual RGV state did not match."));
        }
    }

    private sealed class VehicleLoadAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.vehicle.load.equals";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidOperationException("rgv.vehicle.load.equals Expected must be a string or null.");
            var expected = context.Definition.Expected.ValueKind == JsonValueKind.Null
                ? null
                : context.Definition.Expected.GetString();
            var actual = Runtime(context).GetVehicle(context.Definition.Target).LoadId;
            var passed = string.Equals(expected, actual, StringComparison.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected ?? "null", actual ?? "null",
                passed ? "Virtual RGV load matched." : "Virtual RGV load did not match."));
        }
    }

    private sealed class RouteCompletedAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.route.completed";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("rgv.route.completed Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).GetVehicle(context.Definition.Target).RouteCompleted;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual, expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual RGV route completion matched." : "Virtual RGV route completion did not match."));
        }
    }

    private sealed class SegmentOccupiedByAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.segment.occupied-by";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("rgv.segment.occupied-by Expected must be a vehicle id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var occupancy = Runtime(context).ListOccupancy()
                .FirstOrDefault(item => string.Equals(item.SegmentId, context.Definition.Target, StringComparison.Ordinal));
            var actual = occupancy?.VehicleIds ?? [];
            var passed = actual.Contains(expected, StringComparer.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, string.Join(',', actual),
                passed ? "Virtual RGV segment occupancy matched." : "Virtual RGV segment occupancy did not match."));
        }
    }

    private sealed class BatteryAtLeastAssertionHandler(VirtualRgvOptions options) : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "rgv.vehicle.battery.at-least";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.Number || !context.Definition.Expected.TryGetInt32(out var expected))
                throw new InvalidOperationException("rgv.vehicle.battery.at-least Expected must be an integer.");
            if (expected is < 0 or > 100)
                throw new InvalidOperationException("rgv.vehicle.battery.at-least Expected must be between 0 and 100.");
            var actual = Runtime(context).GetVehicle(context.Definition.Target).BatteryPercent;
            var passed = actual >= expected;
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected.ToString(), actual.ToString(),
                passed ? "Virtual RGV battery threshold passed." : "Virtual RGV battery threshold failed."));
        }
    }

    private sealed class DefineSegmentPayload
    {
        public string FromNodeId { get; set; } = string.Empty;
        public string ToNodeId { get; set; } = string.Empty;
        public int LengthMillimeters { get; set; }
        public int SpeedLimitMillimetersPerSecond { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class DefineVehiclePayload
    {
        public string InitialNodeId { get; set; } = string.Empty;
        public int SpeedMillimetersPerSecond { get; set; }
        public int BatteryPercent { get; set; } = 100;
        public bool IsOnline { get; set; } = true;
        public string? LoadId { get; set; }
        public string Capabilities { get; set; } = nameof(TransportVehicleCapability.Carry);
    }

    private sealed class AssignRoutePayload
    {
        public string[]? SegmentIds { get; set; }
    }

    private sealed class SetOnlinePayload
    {
        public bool IsOnline { get; set; }
    }

    private sealed class LoadPayload
    {
        public string LoadId { get; set; } = string.Empty;
    }

    private sealed class UnloadPayload
    {
        public string? ExpectedLoadId { get; set; }
    }
}
