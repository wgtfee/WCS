namespace Wcs.Simulator.VirtualIntegration;

using System.Text.Json;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

public static class VirtualIntegrationScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions) =>
    [
        new DefineMissionActionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions),
        new DispatchMissionActionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions),
        new AdvanceMissionActionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions),
        new AcknowledgeMissionActionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions)
    ];

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions) =>
    [
        new MissionStateAssertionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions),
        new MissionConsistentAssertionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions),
        new ExternalExactlyOnceAssertionHandler(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions)
    ];

    private abstract class HandlerBase(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
    {
        protected VirtualIntegrationRuntime Runtime(SimulationActionContext context) =>
            new(context.State, options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions,
                context.Random.CaptureState());

        protected VirtualIntegrationRuntime Runtime(SimulationAssertionContext context) =>
            new(context.State, options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions);

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

    private sealed class DefineMissionActionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationActionHandler
    {
        public string Kind => "integration.mission.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineMissionPayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualExternalSystemKind>(payload.ExternalSystemKind, true, out var externalKind))
                throw new InvalidOperationException($"Unsupported S7 external system kind '{payload.ExternalSystemKind}'.");
            var mission = Runtime(context).DefineMission(new VirtualIntegrationMissionDefinition
            {
                MissionId = context.Definition.Target,
                PlcBlockKey = payload.PlcBlockKey,
                VehicleId = payload.VehicleId,
                LoadId = payload.LoadId,
                SourceNodeId = payload.SourceNodeId,
                DestinationNodeId = payload.DestinationNodeId,
                ExternalEndpointId = payload.ExternalEndpointId,
                ExternalSystemKind = externalKind,
                HealthAssetId = payload.HealthAssetId,
                Priority = payload.Priority,
                VehicleSpeedMillimetersPerSecond = payload.VehicleSpeedMillimetersPerSecond,
                VehicleBatteryPercent = payload.VehicleBatteryPercent,
                InitialHealthScore = payload.InitialHealthScore,
                InitialFusionRiskScore = payload.InitialFusionRiskScore,
                Segments = (payload.Segments ?? []).Select(static segment =>
                    new VirtualIntegrationSegmentDefinition(
                        segment.SegmentId,
                        segment.FromNodeId,
                        segment.ToNodeId,
                        segment.LengthMillimeters,
                        segment.SpeedLimitMillimetersPerSecond)).ToArray()
            }, context.Clock.CurrentOffsetMilliseconds, context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(mission)));
        }
    }

    private sealed class DispatchMissionActionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationActionHandler
    {
        public string Kind => "integration.mission.dispatch";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mission = Runtime(context).DispatchMission(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(mission)));
        }
    }

    private sealed class AdvanceMissionActionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationActionHandler
    {
        public string Kind => "integration.mission.advance";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mission = Runtime(context).AdvanceMission(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(mission)));
        }
    }

    private sealed class AcknowledgeMissionActionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationActionHandler
    {
        public string Kind => "integration.mission.ack";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mission = Runtime(context).AcknowledgeMission(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(mission)));
        }
    }

    private sealed class MissionStateAssertionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationAssertionHandler
    {
        public string Kind => "integration.mission.state";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("integration.mission.state Expected must be a state string.");
            var expectedText = context.Definition.Expected.GetString() ?? string.Empty;
            if (!Enum.TryParse<VirtualIntegrationMissionState>(expectedText, true, out var expected))
                throw new InvalidOperationException($"Unsupported S7 mission state '{expectedText}'.");
            var actual = Runtime(context).GetMission(context.Definition.Target).State;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "S7 mission state matched." : "S7 mission state did not match."));
        }
    }

    private sealed class MissionConsistentAssertionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationAssertionHandler
    {
        public string Kind => "integration.mission.consistent";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("integration.mission.consistent Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var consistency = Runtime(context).GetConsistency(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds);
            return ValueTask.FromResult(Assertion(context.Definition, expected == consistency.IsConsistent,
                expected.ToString(), consistency.IsConsistent.ToString(), consistency.Detail));
        }
    }

    private sealed class ExternalExactlyOnceAssertionHandler(
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions)
        : HandlerBase(options, plcOptions, rgvOptions, trafficOptions, externalOptions, healthOptions), ISimulationAssertionHandler
    {
        public string Kind => "integration.external.exactly-once";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("integration.external.exactly-once Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var consistency = Runtime(context).GetConsistency(
                context.Definition.Target,
                context.Clock.CurrentOffsetMilliseconds);
            var actual = consistency.ExternalExactlyOnce && consistency.HealthOutcomeExactlyOnce;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(), consistency.Detail));
        }
    }

    private sealed class DefineMissionPayload
    {
        public string PlcBlockKey { get; set; } = string.Empty;
        public string VehicleId { get; set; } = string.Empty;
        public string LoadId { get; set; } = string.Empty;
        public string SourceNodeId { get; set; } = string.Empty;
        public string DestinationNodeId { get; set; } = string.Empty;
        public string ExternalEndpointId { get; set; } = string.Empty;
        public string ExternalSystemKind { get; set; } = nameof(VirtualExternalSystemKind.Mes);
        public string HealthAssetId { get; set; } = string.Empty;
        public int Priority { get; set; } = 100;
        public int VehicleSpeedMillimetersPerSecond { get; set; } = 1_000;
        public int VehicleBatteryPercent { get; set; } = 100;
        public double InitialHealthScore { get; set; } = 95;
        public double InitialFusionRiskScore { get; set; } = 0.05;
        public SegmentPayload[]? Segments { get; set; }
    }

    private sealed class SegmentPayload
    {
        public string SegmentId { get; set; } = string.Empty;
        public string FromNodeId { get; set; } = string.Empty;
        public string ToNodeId { get; set; } = string.Empty;
        public int LengthMillimeters { get; set; }
        public int SpeedLimitMillimetersPerSecond { get; set; }
    }
}
