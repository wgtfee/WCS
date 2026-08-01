namespace Wcs.Simulator.VirtualHealth;

using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Simulator.ScenarioEngine;

/// <summary>
/// Deterministic process-local synthetic health/RUL validation runtime.
/// It stores only simulation samples, forecast oracles and outcomes in the S1
/// SimulationStateStore. It never loads a model, writes production SQL, or invokes
/// PLC/task/dispatch control paths.
/// </summary>
public sealed partial class VirtualHealthRuntime
{
    private const string AssetCountKey = "__vhealth.asset.count";
    private const string OperationSequenceKey = "__vhealth.operationSequence";
    private const string AuditCountKey = "__vhealth.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualHealthOptions _options;

    public VirtualHealthRuntime(SimulationStateStore state, VirtualHealthOptions options)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public VirtualHealthAssetSnapshot DefineAsset(
        VirtualHealthAssetDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var assetId = NormalizeId(definition.AssetId, nameof(definition.AssetId));
        ValidateHealth(definition.InitialHealthScore, definition.InitialFusionRiskScore, definition.IndependentSourceCount);
        if (_state.Contains(AssetKey(assetId)))
            throw new InvalidOperationException($"Virtual health asset '{assetId}' is already defined.");

        var assetCount = ReadInt64(AssetCountKey);
        if (assetCount >= _options.MaximumAssets)
            throw new InvalidOperationException("Virtual health runtime has reached MaximumAssets.");

        var stored = new AssetStorage(
            assetId,
            definition.InitialHealthScore,
            GradeFor(definition.InitialHealthScore),
            definition.InitialFusionRiskScore,
            FusionStatusFor(definition.InitialFusionRiskScore),
            definition.IndependentSourceCount,
            -1,
            0,
            0,
            0,
            1);
        SetJson(AssetKey(assetId), stored);
        var index = _state.Increment(AssetCountKey, 1);
        SetJson(AssetIndexKey(index), assetId);

        stored = AppendSampleInternal(
            stored,
            definition.InitialHealthScore,
            definition.InitialFusionRiskScore,
            definition.IndependentSourceCount,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            "asset-defined");
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "asset.define", assetId,
            $"health={definition.InitialHealthScore:R};risk={definition.InitialFusionRiskScore:R}", true);
        return ToSnapshot(stored);
    }

    public VirtualHealthSampleSnapshot RecordSample(
        string assetId,
        double healthScore,
        double fusionRiskScore,
        int independentSourceCount,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        string reason = "sample")
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        ValidateHealth(healthScore, fusionRiskScore, independentSourceCount);
        var asset = ReadRequiredAsset(assetId);
        var updated = AppendSampleInternal(
            asset,
            healthScore,
            fusionRiskScore,
            independentSourceCount,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            NormalizeReason(reason));
        var sample = ReadRequiredSample(assetId, updated.SampleCount);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "sample.record", assetId,
            $"health={healthScore:R};risk={fusionRiskScore:R};reason={NormalizeReason(reason)}", true);
        return sample;
    }

    public IReadOnlyList<VirtualHealthSampleSnapshot> GenerateLinearProfile(
        string assetId,
        double targetHealthScore,
        double targetFusionRiskScore,
        long sampleIntervalMilliseconds,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        string reason = "linear-profile")
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        ValidateHealth(targetHealthScore, targetFusionRiskScore, 1);
        if (sampleIntervalMilliseconds < 1)
            throw new InvalidOperationException("Synthetic health sample interval must be positive.");

        var asset = ReadRequiredAsset(assetId);
        if (asset.LastSampleOffsetMilliseconds < 0 || virtualOffsetMilliseconds <= asset.LastSampleOffsetMilliseconds)
            throw new InvalidOperationException("Synthetic linear profile must advance beyond the latest sample offset.");

        var startOffset = asset.LastSampleOffsetMilliseconds;
        var span = checked(virtualOffsetMilliseconds - startOffset);
        var generated = checked((int)((span + sampleIntervalMilliseconds - 1) / sampleIntervalMilliseconds));
        if (generated > _options.MaximumGeneratedSamplesPerAction)
            throw new InvalidOperationException("Synthetic linear profile exceeds MaximumGeneratedSamplesPerAction.");
        if (asset.SampleCount + generated > _options.MaximumSamplesPerAsset)
            throw new InvalidOperationException("Synthetic linear profile exceeds MaximumSamplesPerAsset.");

        var startHealth = asset.HealthScore;
        var startRisk = asset.FusionRiskScore;
        var sourceCount = asset.IndependentSourceCount;
        var result = new List<VirtualHealthSampleSnapshot>(generated);
        var normalizedReason = NormalizeReason(reason);

        for (var index = 1; index <= generated; index++)
        {
            var offset = Math.Min(
                virtualOffsetMilliseconds,
                checked(startOffset + checked(index * sampleIntervalMilliseconds)));
            var fraction = (offset - startOffset) / (double)span;
            var health = Interpolate(startHealth, targetHealthScore, fraction);
            var risk = Interpolate(startRisk, targetFusionRiskScore, fraction);
            var sampleTime = occurredAtUtc - TimeSpan.FromMilliseconds(virtualOffsetMilliseconds - offset);
            asset = AppendSampleInternal(
                asset,
                health,
                risk,
                sourceCount,
                offset,
                sampleTime,
                normalizedReason);
            result.Add(ReadRequiredSample(assetId, asset.SampleCount));
        }

        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "profile.linear", assetId,
            $"samples={generated};targetHealth={targetHealthScore:R};targetRisk={targetFusionRiskScore:R}", true);
        return result;
    }

    public VirtualHealthSampleSnapshot RestoreAfterMaintenance(
        string assetId,
        double restoredHealthScore,
        double restoredFusionRiskScore,
        int independentSourceCount,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        string note = "maintenance")
    {
        var sample = RecordSample(
            assetId,
            restoredHealthScore,
            restoredFusionRiskScore,
            independentSourceCount,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            $"maintenance-{NormalizeReason(note)}");
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "maintenance.restore", assetId,
            $"health={restoredHealthScore:R};risk={restoredFusionRiskScore:R}", true);
        return sample;
    }

    public VirtualHealthForecastOracleSnapshot AddForecastOracle(
        string assetId,
        VirtualHealthForecastOracleDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        assetId = NormalizeId(assetId, nameof(assetId));
        var asset = ReadRequiredAsset(assetId);
        if (asset.ForecastCount >= _options.MaximumForecastsPerAsset)
            throw new InvalidOperationException("Virtual health runtime has reached MaximumForecastsPerAsset.");

        var output = new AssetFailureForecastOutput
        {
            FailureProbability24Hours = definition.FailureProbability24Hours,
            FailureProbability72Hours = definition.FailureProbability72Hours,
            FailureProbability168Hours = definition.FailureProbability168Hours,
            RulLowerHours = definition.RulLowerHours,
            RulMedianHours = definition.RulMedianHours,
            RulUpperHours = definition.RulUpperHours
        };
        AssetFailureForecastManifestValidator.ValidateOutput(output, _options.MaximumRulHours);
        var phase = NormalizeId(definition.Phase, nameof(definition.Phase));
        var sequence = asset.ForecastCount + 1L;
        var snapshot = new VirtualHealthForecastOracleSnapshot(
            sequence,
            $"{assetId}-FC-{sequence:D6}",
            assetId,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            output.FailureProbability24Hours,
            output.FailureProbability72Hours,
            output.FailureProbability168Hours,
            output.RulLowerHours,
            output.RulMedianHours,
            output.RulUpperHours,
            phase);
        SetJson(ForecastKey(assetId, sequence), snapshot);
        SetJson(AssetKey(assetId), asset with
        {
            ForecastCount = checked(asset.ForecastCount + 1),
            Version = asset.Version + 1
        });
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "forecast.oracle", assetId,
            $"phase={phase};p24={output.FailureProbability24Hours:R};rulMedian={output.RulMedianHours:R}", true);
        return snapshot;
    }

    public VirtualHealthOutcomeSnapshot RecordOutcome(
        string assetId,
        VirtualHealthOutcomeKind kind,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        string note)
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        var asset = ReadRequiredAsset(assetId);
        if (asset.OutcomeCount >= _options.MaximumOutcomesPerAsset)
            throw new InvalidOperationException("Virtual health runtime has reached MaximumOutcomesPerAsset.");
        var sequence = asset.OutcomeCount + 1L;
        var snapshot = new VirtualHealthOutcomeSnapshot(
            sequence,
            $"{assetId}-OUT-{sequence:D6}",
            assetId,
            kind,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            NormalizeReason(note));
        SetJson(OutcomeKey(assetId, sequence), snapshot);
        SetJson(AssetKey(assetId), asset with
        {
            OutcomeCount = checked(asset.OutcomeCount + 1),
            Version = asset.Version + 1
        });
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "outcome.record", assetId,
            $"kind={kind};note={snapshot.Note}", true);
        return snapshot;
    }

    public VirtualHealthAssetSnapshot GetAsset(string assetId) =>
        ToSnapshot(ReadRequiredAsset(NormalizeId(assetId, nameof(assetId))));

    public IReadOnlyList<VirtualHealthAssetSnapshot> ListAssets() =>
        Enumerable.Range(1, checked((int)ReadInt64(AssetCountKey)))
            .Select(index => ReadRequiredJson<string>(AssetIndexKey(index)))
            .Select(ReadRequiredAsset)
            .Select(ToSnapshot)
            .OrderBy(static item => item.AssetId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<VirtualHealthSampleSnapshot> ListSamples(string assetId) =>
        ReadSequence(assetId, ReadRequiredAsset(NormalizeId(assetId, nameof(assetId))).SampleCount, ReadRequiredSample);

    public IReadOnlyList<VirtualHealthForecastOracleSnapshot> ListForecasts(string assetId)
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        var asset = ReadRequiredAsset(assetId);
        return ReadSequence(assetId, asset.ForecastCount, ReadRequiredForecast);
    }

    public IReadOnlyList<VirtualHealthOutcomeSnapshot> ListOutcomes(string assetId)
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        var asset = ReadRequiredAsset(assetId);
        return ReadSequence(assetId, asset.OutcomeCount, ReadRequiredOutcome);
    }

    public VirtualHealthFeatureSnapshot GetFeatureSnapshot(string assetId)
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        var samples = ListSamples(assetId);
        var points = samples.Select(ToHealthPoint).ToArray();
        var forecastOptions = new AssetFailureForecastOptions
        {
            MinimumHistoryPoints = _options.ForecastMinimumHistoryPoints,
            MinimumHistorySpanHours = _options.ForecastMinimumHistorySpanHours,
            MaximumHistoryPoints = _options.ForecastMaximumHistoryPoints
        };
        var valid = AssetFailureForecastFeatureBuilder.TryBuild(
            assetId,
            points,
            forecastOptions,
            out var vector,
            out var reason);
        return new VirtualHealthFeatureSnapshot(
            assetId,
            valid,
            reason,
            vector?.WindowStartUtc,
            vector?.WindowEndUtc,
            vector?.SampleCount ?? points.Length,
            vector?.HistorySpanHours ?? (points.Length > 1 ? (points[^1].RecordedAtUtc - points[0].RecordedAtUtc).TotalHours : 0),
            vector?.FeatureNames?.ToArray() ?? Array.Empty<string>(),
            vector?.Values?.ToArray() ?? Array.Empty<double>());
    }

    public VirtualHealthTrendSnapshot GetTrend(string assetId)
    {
        assetId = NormalizeId(assetId, nameof(assetId));
        var samples = ListSamples(assetId).TakeLast(_options.TrendWindowSize).ToArray();
        if (samples.Length < 2)
            throw new InvalidOperationException("At least two synthetic health samples are required for a trend.");
        var scores = samples.Select(static item => item.HealthScore).ToArray();
        var delta = scores[^1] - scores[0];
        var direction = delta <= -_options.TrendChangeThreshold
            ? AssetHealthTrendDirection.Deteriorating
            : delta >= _options.TrendChangeThreshold
                ? AssetHealthTrendDirection.Improving
                : AssetHealthTrendDirection.Stable;
        return new VirtualHealthTrendSnapshot(
            assetId,
            direction,
            scores[^1],
            delta,
            scores.Average(),
            scores.Min(),
            scores.Max(),
            CalculateSlopePerHour(samples),
            samples.Length,
            samples[^1].Grade,
            samples[0].RecordedAtUtc,
            samples[^1].RecordedAtUtc);
    }

    public bool ForecastContractsValid(string assetId)
    {
        try
        {
            foreach (var forecast in ListForecasts(assetId))
            {
                AssetFailureForecastManifestValidator.ValidateOutput(
                    new AssetFailureForecastOutput
                    {
                        FailureProbability24Hours = forecast.FailureProbability24Hours,
                        FailureProbability72Hours = forecast.FailureProbability72Hours,
                        FailureProbability168Hours = forecast.FailureProbability168Hours,
                        RulLowerHours = forecast.RulLowerHours,
                        RulMedianHours = forecast.RulMedianHours,
                        RulUpperHours = forecast.RulUpperHours
                    },
                    _options.MaximumRulHours);
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool RulMedianIsNonIncreasing(string assetId, string? phase = null)
    {
        var forecasts = FilterPhase(ListForecasts(assetId), phase);
        return IsNonIncreasing(forecasts.Select(static item => item.RulMedianHours));
    }

    public bool FailureProbabilitiesAreNonDecreasing(string assetId, string? phase = null)
    {
        var forecasts = FilterPhase(ListForecasts(assetId), phase);
        return IsNonDecreasing(forecasts.Select(static item => item.FailureProbability24Hours)) &&
               IsNonDecreasing(forecasts.Select(static item => item.FailureProbability72Hours)) &&
               IsNonDecreasing(forecasts.Select(static item => item.FailureProbability168Hours));
    }

    public IReadOnlyList<VirtualHealthAuditRecord> ListAudit()
    {
        var total = Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords);
        if (total <= 0)
            return [];
        var sequence = ReadInt64(OperationSequenceKey);
        var first = Math.Max(1, sequence - total + 1);
        var result = new List<VirtualHealthAuditRecord>((int)total);
        for (var current = first; current <= sequence; current++)
        {
            var slot = (int)((current - 1) % _options.MaximumAuditRecords);
            if (TryReadJson<VirtualHealthAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == current)
                result.Add(record);
        }
        return result;
    }

    public VirtualHealthStatus GetStatus()
    {
        var assets = ListAssets();
        return new VirtualHealthStatus(
            assets.Count,
            assets.Sum(static item => item.SampleCount),
            assets.Sum(static item => item.ForecastCount),
            assets.Sum(static item => item.OutcomeCount),
            (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords),
            ReadInt64(OperationSequenceKey));
    }

    private AssetStorage AppendSampleInternal(
        AssetStorage asset,
        double healthScore,
        double fusionRiskScore,
        int independentSourceCount,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc,
        string reason)
    {
        ValidateHealth(healthScore, fusionRiskScore, independentSourceCount);
        if (virtualOffsetMilliseconds < 0 || virtualOffsetMilliseconds < asset.LastSampleOffsetMilliseconds)
            throw new InvalidOperationException("Synthetic health sample offset cannot move backwards.");
        if (asset.SampleCount >= _options.MaximumSamplesPerAsset)
            throw new InvalidOperationException("Virtual health runtime has reached MaximumSamplesPerAsset.");

        var previousHealth = asset.SampleCount == 0 ? healthScore : asset.HealthScore;
        var previousGrade = asset.SampleCount == 0 ? GradeFor(healthScore) : asset.Grade;
        var grade = GradeFor(healthScore);
        var delta = healthScore - previousHealth;
        var direction = delta > 1e-12
            ? AssetHealthTrendDirection.Improving
            : delta < -1e-12
                ? AssetHealthTrendDirection.Deteriorating
                : AssetHealthTrendDirection.Stable;
        var sequence = asset.SampleCount + 1L;
        var sample = new VirtualHealthSampleSnapshot(
            sequence,
            asset.AssetId,
            virtualOffsetMilliseconds,
            occurredAtUtc,
            healthScore,
            previousHealth,
            delta,
            grade,
            previousGrade,
            grade != previousGrade,
            direction,
            fusionRiskScore,
            FusionStatusFor(fusionRiskScore),
            independentSourceCount,
            reason);
        SetJson(SampleKey(asset.AssetId, sequence), sample);
        var updated = asset with
        {
            HealthScore = healthScore,
            Grade = grade,
            FusionRiskScore = fusionRiskScore,
            FusionStatus = sample.FusionStatus,
            IndependentSourceCount = independentSourceCount,
            LastSampleOffsetMilliseconds = virtualOffsetMilliseconds,
            SampleCount = checked(asset.SampleCount + 1),
            Version = asset.Version + 1
        };
        SetJson(AssetKey(asset.AssetId), updated);
        return updated;
    }

    private AssetHealthScorePoint ToHealthPoint(VirtualHealthSampleSnapshot sample) => new()
    {
        Sequence = sample.Sequence,
        AssetId = sample.AssetId,
        HealthScore = sample.HealthScore,
        PreviousHealthScore = sample.PreviousHealthScore,
        ScoreDelta = sample.ScoreDelta,
        Grade = sample.Grade,
        PreviousGrade = sample.PreviousGrade,
        GradeChanged = sample.GradeChanged,
        Direction = sample.Direction,
        FusionRiskScore = sample.FusionRiskScore,
        FusionStatus = sample.FusionStatus,
        IndependentSourceCount = sample.IndependentSourceCount,
        CalculatedAtUtc = sample.RecordedAtUtc.UtcDateTime,
        RecordedAtUtc = sample.RecordedAtUtc.UtcDateTime,
        Summary = $"S6 synthetic health sample: {sample.Reason}"
    };

    private AssetHealthGrade GradeFor(double score) => score >= _options.HealthyMinimumScore
        ? AssetHealthGrade.Healthy
        : score >= _options.AttentionMinimumScore
            ? AssetHealthGrade.Attention
            : score >= _options.DegradedMinimumScore
                ? AssetHealthGrade.Degraded
                : AssetHealthGrade.Critical;

    private static FusedHealthStatus FusionStatusFor(double risk) => risk < 0.35
        ? FusedHealthStatus.Normal
        : risk < 0.65
            ? FusedHealthStatus.Observe
            : risk < 0.85
                ? FusedHealthStatus.Warning
                : FusedHealthStatus.Alarm;

    private void ValidateHealth(double healthScore, double fusionRiskScore, int independentSourceCount)
    {
        if (!double.IsFinite(healthScore) || healthScore is < 0 or > 100)
            throw new InvalidOperationException("Synthetic HealthScore must be finite and between 0 and 100.");
        if (!double.IsFinite(fusionRiskScore) || fusionRiskScore is < 0 or > 1)
            throw new InvalidOperationException("Synthetic FusionRiskScore must be finite and between 0 and 1.");
        if (independentSourceCount is < 0 or > 1_000)
            throw new InvalidOperationException("Synthetic IndependentSourceCount must be between 0 and 1,000.");
    }

    private void AppendAudit(
        DateTimeOffset occurredAtUtc,
        long virtualOffsetMilliseconds,
        string operation,
        string target,
        string? detail,
        bool success)
    {
        var sequence = _state.Increment(OperationSequenceKey, 1);
        var record = new VirtualHealthAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            operation,
            target,
            detail,
            success);
        var slot = (int)((sequence - 1) % _options.MaximumAuditRecords);
        SetJson(AuditSlotKey(slot), record);
        var retained = Math.Min(_state.Increment(AuditCountKey, 1), _options.MaximumAuditRecords);
        if (retained == _options.MaximumAuditRecords)
            SetLong(AuditCountKey, retained);
    }

    private AssetStorage ReadRequiredAsset(string assetId) =>
        ReadRequiredJson<AssetStorage>(AssetKey(assetId));

    private VirtualHealthSampleSnapshot ReadRequiredSample(string assetId, long sequence) =>
        ReadRequiredJson<VirtualHealthSampleSnapshot>(SampleKey(assetId, sequence));

    private VirtualHealthForecastOracleSnapshot ReadRequiredForecast(string assetId, long sequence) =>
        ReadRequiredJson<VirtualHealthForecastOracleSnapshot>(ForecastKey(assetId, sequence));

    private VirtualHealthOutcomeSnapshot ReadRequiredOutcome(string assetId, long sequence) =>
        ReadRequiredJson<VirtualHealthOutcomeSnapshot>(OutcomeKey(assetId, sequence));

    private T ReadRequiredJson<T>(string key)
    {
        if (!TryReadJson<T>(key, out var value))
            throw new KeyNotFoundException($"Virtual health state '{key}' was not found.");
        return value;
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

    private void SetJson<T>(string key, T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        _state.Set(key, document.RootElement);
    }

    private void SetLong(string key, long value)
    {
        using var document = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _state.Set(key, document.RootElement);
    }

    private long ReadInt64(string key)
    {
        if (!_state.TryGet(key, out var element))
            return 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new InvalidOperationException($"Virtual health counter '{key}' is invalid.");
        return value;
    }

    private static IReadOnlyList<T> ReadSequence<T>(
        string assetId,
        int count,
        Func<string, long, T> reader)
    {
        if (count <= 0)
            return [];
        var result = new T[count];
        for (var index = 1; index <= count; index++)
            result[index - 1] = reader(assetId, index);
        return result;
    }

    private static IReadOnlyList<VirtualHealthForecastOracleSnapshot> FilterPhase(
        IReadOnlyList<VirtualHealthForecastOracleSnapshot> forecasts,
        string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
            return forecasts;
        return forecasts.Where(item => string.Equals(item.Phase, phase.Trim(), StringComparison.Ordinal)).ToArray();
    }

    private static bool IsNonIncreasing(IEnumerable<double> values)
    {
        var items = values.ToArray();
        if (items.Length < 2) return true;
        for (var index = 1; index < items.Length; index++)
            if (items[index] > items[index - 1] + 1e-12) return false;
        return true;
    }

    private static bool IsNonDecreasing(IEnumerable<double> values)
    {
        var items = values.ToArray();
        if (items.Length < 2) return true;
        for (var index = 1; index < items.Length; index++)
            if (items[index] + 1e-12 < items[index - 1]) return false;
        return true;
    }

    private static double CalculateSlopePerHour(IReadOnlyList<VirtualHealthSampleSnapshot> samples)
    {
        var anchor = samples[0].RecordedAtUtc;
        var x = samples.Select(item => (item.RecordedAtUtc - anchor).TotalHours).ToArray();
        var y = samples.Select(static item => item.HealthScore).ToArray();
        var xMean = x.Average();
        var yMean = y.Average();
        var denominator = x.Sum(value => Math.Pow(value - xMean, 2));
        if (denominator <= 1e-12) return 0;
        return x.Zip(y, (time, score) => (time - xMean) * (score - yMean)).Sum() / denominator;
    }

    private static double Interpolate(double start, double end, double fraction) =>
        start + ((end - start) * fraction);

    private static string NormalizeId(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!IdentifierRegex().IsMatch(normalized))
            throw new InvalidOperationException($"Virtual health {name} contains unsupported characters.");
        return normalized;
    }

    private static string NormalizeReason(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return "unspecified";
        if (normalized.Length > 256)
            throw new InvalidOperationException("Virtual health reason cannot exceed 256 characters.");
        return normalized;
    }

    private static VirtualHealthAssetSnapshot ToSnapshot(AssetStorage asset) => new(
        asset.AssetId,
        asset.HealthScore,
        asset.Grade,
        asset.FusionRiskScore,
        asset.FusionStatus,
        asset.IndependentSourceCount,
        asset.LastSampleOffsetMilliseconds,
        asset.SampleCount,
        asset.ForecastCount,
        asset.OutcomeCount,
        asset.Version);

    private static string AssetKey(string assetId) => $"__vhealth.asset.{assetId}";
    private static string AssetIndexKey(long index) => $"__vhealth.assetIndex.{index:D6}";
    private static string SampleKey(string assetId, long sequence) => $"__vhealth.sample.{assetId}.{sequence:D6}";
    private static string ForecastKey(string assetId, long sequence) => $"__vhealth.forecast.{assetId}.{sequence:D6}";
    private static string OutcomeKey(string assetId, long sequence) => $"__vhealth.outcome.{assetId}.{sequence:D6}";
    private static string AuditSlotKey(int slot) => $"__vhealth.audit.{slot:D6}";

    private sealed record AssetStorage(
        string AssetId,
        double HealthScore,
        AssetHealthGrade Grade,
        double FusionRiskScore,
        FusedHealthStatus FusionStatus,
        int IndependentSourceCount,
        long LastSampleOffsetMilliseconds,
        int SampleCount,
        int ForecastCount,
        int OutcomeCount,
        long Version);
}
