namespace Wcs.Simulator.VirtualHealth;

using System.Text.Json;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Simulator.ScenarioEngine;

public static class VirtualHealthScenarioHandlers
{
    public static IReadOnlyList<ISimulationActionHandler> CreateActions(VirtualHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new DefineAssetActionHandler(options),
            new RecordSampleActionHandler(options),
            new LinearProfileActionHandler(options),
            new MaintenanceRestoreActionHandler(options),
            new ForecastOracleActionHandler(options),
            new RecordOutcomeActionHandler(options)
        ];
    }

    public static IReadOnlyList<ISimulationAssertionHandler> CreateAssertions(VirtualHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return
        [
            new AssetGradeAssertionHandler(options),
            new AssetScoreAtMostAssertionHandler(options),
            new SampleCountAssertionHandler(options),
            new TrendDirectionAssertionHandler(options),
            new FeatureValidAssertionHandler(options),
            new ForecastContractAssertionHandler(options),
            new RulNonIncreasingAssertionHandler(options),
            new ProbabilityNonDecreasingAssertionHandler(options),
            new OutcomeKindAssertionHandler(options)
        ];
    }

    private abstract class HandlerBase(VirtualHealthOptions options)
    {
        protected VirtualHealthOptions Options { get; } = options;
        protected VirtualHealthRuntime Runtime(SimulationActionContext context) => new(context.State, Options);
        protected VirtualHealthRuntime Runtime(SimulationAssertionContext context) => new(context.State, Options);

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

    private sealed class DefineAssetActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.asset.define";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<DefineAssetPayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).DefineAsset(
                new VirtualHealthAssetDefinition(
                    context.Definition.Target,
                    payload.InitialHealthScore,
                    payload.InitialFusionRiskScore,
                    payload.IndependentSourceCount),
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class RecordSampleActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.sample.record";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<RecordSamplePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).RecordSample(
                context.Definition.Target,
                payload.HealthScore,
                payload.FusionRiskScore,
                payload.IndependentSourceCount,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc,
                payload.Reason);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class LinearProfileActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.profile.linear";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<LinearProfilePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).GenerateLinearProfile(
                context.Definition.Target,
                payload.TargetHealthScore,
                payload.TargetFusionRiskScore,
                payload.SampleIntervalMilliseconds,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc,
                payload.Reason);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
            {
                generated = result.Count,
                last = result.LastOrDefault()
            })));
        }
    }

    private sealed class MaintenanceRestoreActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.maintenance.restore";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<MaintenanceRestorePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).RestoreAfterMaintenance(
                context.Definition.Target,
                payload.HealthScore,
                payload.FusionRiskScore,
                payload.IndependentSourceCount,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc,
                payload.Note);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class ForecastOracleActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.forecast.oracle";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<ForecastOraclePayload>(context.Definition.Payload, Kind);
            var result = Runtime(context).AddForecastOracle(
                context.Definition.Target,
                new VirtualHealthForecastOracleDefinition(
                    payload.FailureProbability24Hours,
                    payload.FailureProbability72Hours,
                    payload.FailureProbability168Hours,
                    payload.RulLowerHours,
                    payload.RulMedianHours,
                    payload.RulUpperHours,
                    payload.Phase),
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class RecordOutcomeActionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationActionHandler
    {
        public string Kind => "health.outcome.record";

        public ValueTask<SimulationActionOutcome> ExecuteAsync(
            SimulationActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadPayload<RecordOutcomePayload>(context.Definition.Payload, Kind);
            if (!Enum.TryParse<VirtualHealthOutcomeKind>(payload.Kind, true, out var kind))
                throw new InvalidOperationException($"Unsupported virtual health outcome kind '{payload.Kind}'.");
            var result = Runtime(context).RecordOutcome(
                context.Definition.Target,
                kind,
                context.Clock.CurrentOffsetMilliseconds,
                context.Clock.CurrentTimeUtc,
                payload.Note);
            return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(result)));
        }
    }

    private sealed class AssetGradeAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.asset.grade";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("health.asset.grade Expected must be a grade string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetAsset(context.Definition.Target).Grade.ToString();
            var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Synthetic health grade matched." : "Synthetic health grade did not match."));
        }
    }

    private sealed class AssetScoreAtMostAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.asset.score.at-most";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.Number ||
                !context.Definition.Expected.TryGetDouble(out var expected))
                throw new InvalidOperationException("health.asset.score.at-most Expected must be numeric.");
            var actual = Runtime(context).GetAsset(context.Definition.Target).HealthScore;
            return ValueTask.FromResult(Assertion(context.Definition, actual <= expected,
                expected.ToString("R"), actual.ToString("R"),
                actual <= expected ? "Synthetic health score is within the expected upper bound." : "Synthetic health score exceeded the expected upper bound."));
        }
    }

    private sealed class SampleCountAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.sample.count";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.Number ||
                !context.Definition.Expected.TryGetInt32(out var expected))
                throw new InvalidOperationException("health.sample.count Expected must be an integer.");
            var actual = Runtime(context).GetAsset(context.Definition.Target).SampleCount;
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                expected == actual ? "Synthetic health sample count matched." : "Synthetic health sample count did not match."));
        }
    }

    private sealed class TrendDirectionAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.trend.direction";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("health.trend.direction Expected must be a direction string.");
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var actual = Runtime(context).GetTrend(context.Definition.Target).Direction.ToString();
            var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Synthetic health trend direction matched." : "Synthetic health trend direction did not match."));
        }
    }

    private sealed class FeatureValidAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.feature.valid";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("health.feature.valid Expected must be a boolean.");
            var expected = context.Definition.Expected.GetBoolean();
            var feature = Runtime(context).GetFeatureSnapshot(context.Definition.Target);
            return ValueTask.FromResult(Assertion(context.Definition, expected == feature.Valid,
                expected.ToString(), feature.Valid.ToString(),
                feature.Valid ? "Synthetic history satisfies the governed v3.9 feature schema." : feature.Reason ?? "Synthetic feature vector is invalid."));
        }
    }

    private sealed class ForecastContractAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.forecast.contract.valid";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = ReadBooleanExpected(context.Definition, Kind);
            var actual = Runtime(context).ForecastContractsValid(context.Definition.Target);
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                actual ? "Synthetic forecast oracles satisfy the governed v3.9 output contract." : "Synthetic forecast oracle contract validation failed."));
        }
    }

    private sealed class RulNonIncreasingAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.rul.nonincreasing";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = ReadBooleanExpected(context.Definition, Kind);
            var actual = Runtime(context).RulMedianIsNonIncreasing(context.Definition.Target);
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                actual ? "Synthetic RUL median is non-increasing." : "Synthetic RUL median increased unexpectedly."));
        }
    }

    private sealed class ProbabilityNonDecreasingAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.probability.nondecreasing";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = ReadBooleanExpected(context.Definition, Kind);
            var actual = Runtime(context).FailureProbabilitiesAreNonDecreasing(context.Definition.Target);
            return ValueTask.FromResult(Assertion(context.Definition, expected == actual,
                expected.ToString(), actual.ToString(),
                actual ? "Synthetic failure probabilities are non-decreasing." : "Synthetic failure probabilities decreased unexpectedly."));
        }
    }

    private sealed class OutcomeKindAssertionHandler(VirtualHealthOptions options)
        : HandlerBase(options), ISimulationAssertionHandler
    {
        public string Kind => "health.outcome.kind";

        public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
            SimulationAssertionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Definition.Expected.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("health.outcome.kind Expected must be an outcome kind string.");
            var outcomes = Runtime(context).ListOutcomes(context.Definition.Target);
            var actual = outcomes.LastOrDefault()?.Kind.ToString() ?? "None";
            var expected = context.Definition.Expected.GetString() ?? string.Empty;
            var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(Assertion(context.Definition, passed, expected, actual,
                passed ? "Synthetic outcome kind matched." : "Synthetic outcome kind did not match."));
        }
    }

    private static bool ReadBooleanExpected(SimulationAssertionDefinition definition, string kind)
    {
        if (definition.Expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"{kind} Expected must be a boolean.");
        return definition.Expected.GetBoolean();
    }

    private sealed class DefineAssetPayload
    {
        public double InitialHealthScore { get; set; } = 100;
        public double InitialFusionRiskScore { get; set; }
        public int IndependentSourceCount { get; set; } = 1;
    }

    private sealed class RecordSamplePayload
    {
        public double HealthScore { get; set; }
        public double FusionRiskScore { get; set; }
        public int IndependentSourceCount { get; set; } = 1;
        public string Reason { get; set; } = "sample";
    }

    private sealed class LinearProfilePayload
    {
        public double TargetHealthScore { get; set; }
        public double TargetFusionRiskScore { get; set; }
        public long SampleIntervalMilliseconds { get; set; } = 3_600_000;
        public string Reason { get; set; } = "linear-profile";
    }

    private sealed class MaintenanceRestorePayload
    {
        public double HealthScore { get; set; } = 90;
        public double FusionRiskScore { get; set; } = 0.1;
        public int IndependentSourceCount { get; set; } = 1;
        public string Note { get; set; } = "maintenance";
    }

    private sealed class ForecastOraclePayload
    {
        public double FailureProbability24Hours { get; set; }
        public double FailureProbability72Hours { get; set; }
        public double FailureProbability168Hours { get; set; }
        public double RulLowerHours { get; set; }
        public double RulMedianHours { get; set; }
        public double RulUpperHours { get; set; }
        public string Phase { get; set; } = "degradation";
    }

    private sealed class RecordOutcomePayload
    {
        public string Kind { get; set; } = nameof(VirtualHealthOutcomeKind.CensoredNoFailure);
        public string Note { get; set; } = "synthetic-outcome";
    }
}
