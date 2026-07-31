namespace Wcs.Simulator.VirtualTraffic;

using System.Text.Json;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;

public static class VirtualTrafficScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rgvOptions);
        options.Validate();
        rgvOptions.Validate();
        return
        [
            new DefineZoneActionHandler(options, rgvOptions),
            new ReserveActionHandler(options, rgvOptions),
            new ReleaseActionHandler(options, rgvOptions),
            new ExpireActionHandler(options, rgvOptions),
            new RollingReserveActionHandler(options, rgvOptions),
            new RollingReleaseActionHandler(options, rgvOptions),
            new DetectDeadlockActionHandler(options, rgvOptions),
            new ResolveDeadlockActionHandler(options, rgvOptions)
        ];
    }

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rgvOptions);
        options.Validate();
        rgvOptions.Validate();
        return
        [
            new ReservationOwnedByAssertionHandler(options, rgvOptions),
            new RequestWaitingAssertionHandler(options, rgvOptions),
            new ConflictExistsAssertionHandler(options, rgvOptions),
            new WaitsForAssertionHandler(options, rgvOptions),
            new DeadlockExistsAssertionHandler(options, rgvOptions),
            new DeadlockVictimAssertionHandler(options, rgvOptions),
            new ZoneAvailableAssertionHandler(options, rgvOptions)
        ];
    }

    private abstract class HandlerBase(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions)
    {
        protected VirtualTrafficOptions Options { get; } = options;
        protected VirtualRgvOptions RgvOptions { get; } = rgvOptions;

        protected VirtualTrafficRuntime Runtime(SimulationActionContext context) =>
            new(context.State, Options, RgvOptions);

        protected VirtualTrafficRuntime Runtime(SimulationAssertionContext context) =>
            new(context.State, Options, RgvOptions);

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

    private sealed class DefineZoneActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.zone.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineZonePayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualTrafficZoneKind>(payload.Kind, true, out var kind))
                throw new InvalidOperationException($"Unsupported virtual traffic zone kind '{payload.Kind}'.");
            var snapshot = Runtime(context).DefineZone(new VirtualTrafficZoneDefinition
            {
                ZoneId = context.Definition.Target,
                SegmentIds = payload.SegmentIds ?? [],
                Capacity = payload.Capacity,
                Kind = kind
            }, context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(snapshot)));
        }
    }

    private sealed class ReserveActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.reserve";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ReservePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).RequestReservation(context.Definition.Target,
                payload.SegmentId, payload.Priority, payload.LeaseMilliseconds,
                context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ReleaseActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.release";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ReleasePayload>(context.Definition.Payload, Kind);
            var released = Runtime(context).ReleaseReservation(context.Definition.Target,
                payload.SegmentId, context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new { released })));
        }
    }

    private sealed class ExpireActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.expire";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expired = Runtime(context).ExpireReservations(context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new { expired })));
        }
    }

    private sealed class RollingReserveActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.rolling.reserve";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<RollingReservePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).ReserveRollingWindow(context.Definition.Target,
                payload.LookAheadSegments, payload.Priority, payload.LeaseMilliseconds,
                context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class RollingReleaseActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.rolling.release";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var released = Runtime(context).ReleasePassedReservations(context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(released)));
        }
    }

    private sealed class DetectDeadlockActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.deadlock.detect";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deadlocks = Runtime(context).DetectDeadlocks(context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(deadlocks)));
        }
    }

    private sealed class ResolveDeadlockActionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationActionHandler
    {
        public string Kind => "traffic.deadlock.resolve";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Runtime(context).ResolveDeadlock(context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ReservationOwnedByAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.reservation.owned-by";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("traffic.reservation.owned-by Expected must be a vehicle id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var owners = Runtime(context).ListReservations(true, context.Clock.CurrentOffsetMilliseconds)
                .Where(item => string.Equals(item.SegmentId, context.Definition.Target, StringComparison.Ordinal))
                .Select(static item => item.VehicleId)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            var passed = owners.Contains(expected, StringComparer.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, string.Join(',', owners),
                passed ? "Virtual traffic reservation owner matched." : "Virtual traffic reservation owner did not match."));
        }
    }

    private sealed class RequestWaitingAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.request.waiting";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("traffic.request.waiting Expected must be a segment id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var waiting = Runtime(context).ListWaitingRequests(true)
                .Where(item => string.Equals(item.VehicleId, context.Definition.Target, StringComparison.Ordinal))
                .Select(static item => item.SegmentId)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            var passed = waiting.Contains(expected, StringComparer.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, string.Join(',', waiting),
                passed ? "Virtual traffic waiting request matched." : "Virtual traffic waiting request did not match."));
        }
    }

    private sealed class ConflictExistsAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.conflict.exists";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("traffic.conflict.exists Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).ListWaitingRequests(true)
                .Any(item => string.Equals(item.ZoneId, context.Definition.Target, StringComparison.Ordinal));
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual traffic conflict existence matched." : "Virtual traffic conflict existence did not match."));
        }
    }

    private sealed class WaitsForAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.waits-for";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("traffic.waits-for Expected must be a blocking vehicle id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var blockers = Runtime(context).ListWaitEdges()
                .Where(item => string.Equals(item.WaitingVehicleId, context.Definition.Target, StringComparison.Ordinal))
                .Select(static item => item.BlockingVehicleId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            var passed = blockers.Contains(expected, StringComparer.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, string.Join(',', blockers),
                passed ? "Virtual traffic wait edge matched." : "Virtual traffic wait edge did not match."));
        }
    }

    private sealed class DeadlockExistsAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.deadlock.exists";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("traffic.deadlock.exists Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var actual = Runtime(context).ListDeadlocks(true).Count > 0;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual traffic deadlock existence matched." : "Virtual traffic deadlock existence did not match."));
        }
    }

    private sealed class DeadlockVictimAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.deadlock.victim";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("traffic.deadlock.victim Expected must be a vehicle id string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetDeadlock(context.Definition.Target).VictimVehicleId;
            var passed = string.Equals(expected, actual, StringComparison.Ordinal);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Virtual traffic deadlock victim matched." : "Virtual traffic deadlock victim did not match."));
        }
    }

    private sealed class ZoneAvailableAssertionHandler(
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions) : HandlerBase(options, rgvOptions), ISimulationAssertionHandler
    {
        public string Kind => "traffic.zone.available";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("traffic.zone.available Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var zone = Runtime(context).GetZone(context.Definition.Target);
            var occupied = Runtime(context).ListReservations(true, context.Clock.CurrentOffsetMilliseconds)
                .Count(item => string.Equals(item.ZoneId, zone.ZoneId, StringComparison.Ordinal));
            var actual = occupied < zone.Capacity;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Virtual traffic zone availability matched." : "Virtual traffic zone availability did not match."));
        }
    }

    private sealed class DefineZonePayload
    {
        public string[]? SegmentIds { get; set; }
        public int Capacity { get; set; } = 1;
        public string Kind { get; set; } = nameof(VirtualTrafficZoneKind.SharedSegment);
    }

    private sealed class ReservePayload
    {
        public string SegmentId { get; set; } = string.Empty;
        public int Priority { get; set; } = 100;
        public long? LeaseMilliseconds { get; set; }
    }

    private sealed class ReleasePayload
    {
        public string SegmentId { get; set; } = string.Empty;
    }

    private sealed class RollingReservePayload
    {
        public int LookAheadSegments { get; set; } = 1;
        public int Priority { get; set; } = 100;
        public long? LeaseMilliseconds { get; set; }
    }
}
