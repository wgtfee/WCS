namespace Wcs.Core.AnomalyDetection.Forecasting;

using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection.HealthScoring;

public static class AssetFailureForecastFeatureSchema
{
    public static readonly string[] Names =
    {
        "health.latest",
        "health.mean",
        "health.minimum",
        "health.maximum",
        "health.stddev",
        "health.slopePerHour",
        "health.delta",
        "fusionRisk.mean",
        "fusionRisk.maximum",
        "grade.changeCount",
        "grade.degradedOrWorseRatio",
        "grade.criticalRatio",
        "history.sampleCount",
        "history.spanHours"
    };
}

public static class AssetFailureForecastFeatureBuilder
{
    public static bool TryBuild(
        string assetId,
        IReadOnlyList<AssetHealthScorePoint> history,
        AssetFailureForecastOptions options,
        out AssetFailureForecastFeatureVector? vector,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);
        vector = null;
        reason = null;

        var normalizedAssetId = assetId?.Trim() ?? string.Empty;
        if (normalizedAssetId.Length == 0)
        {
            reason = "AssetId is required.";
            return false;
        }

        var ordered = history
            .Where(static point => point.RecordedAtUtc != default && double.IsFinite(point.HealthScore))
            .OrderBy(static point => point.RecordedAtUtc)
            .TakeLast(Math.Clamp(options.MaximumHistoryPoints, 2, 100_000))
            .ToArray();
        if (ordered.Length < Math.Clamp(options.MinimumHistoryPoints, 2, options.MaximumHistoryPoints))
        {
            reason = $"At least {options.MinimumHistoryPoints} retained health points are required.";
            return false;
        }

        var start = ordered[0].RecordedAtUtc;
        var end = ordered[^1].RecordedAtUtc;
        var spanHours = (end - start).TotalHours;
        if (!double.IsFinite(spanHours) || spanHours < options.MinimumHistorySpanHours)
        {
            reason = $"At least {options.MinimumHistorySpanHours} hours of health history are required.";
            return false;
        }

        var scores = ordered.Select(static point => point.HealthScore).ToArray();
        var risks = ordered.Select(static point => point.FusionRiskScore).ToArray();
        if (scores.Any(static value => !double.IsFinite(value) || value is < 0 or > 100) ||
            risks.Any(static value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            reason = "Health history contains values outside the governed range.";
            return false;
        }

        var mean = scores.Average();
        var variance = scores.Select(value => Math.Pow(value - mean, 2)).Average();
        var slope = CalculateSlopePerHour(ordered);
        var gradeChangeCount = ordered.Skip(1)
            .Zip(ordered, static (current, previous) => current.Grade != previous.Grade ? 1 : 0)
            .Sum();
        var degradedOrWorse = ordered.Count(static point => point.Grade >= AssetHealthGrade.Degraded) / (double)ordered.Length;
        var critical = ordered.Count(static point => point.Grade == AssetHealthGrade.Critical) / (double)ordered.Length;

        var values = new[]
        {
            scores[^1],
            mean,
            scores.Min(),
            scores.Max(),
            Math.Sqrt(Math.Max(0, variance)),
            slope,
            scores[^1] - scores[0],
            risks.Average(),
            risks.Max(),
            (double)gradeChangeCount,
            degradedOrWorse,
            critical,
            ordered.Length,
            spanHours
        };
        if (values.Any(static value => !double.IsFinite(value)))
        {
            reason = "Forecast feature vector contains a non-finite value.";
            return false;
        }

        vector = new AssetFailureForecastFeatureVector
        {
            AssetId = normalizedAssetId,
            WindowStartUtc = start,
            WindowEndUtc = end,
            FeatureNames = AssetFailureForecastFeatureSchema.Names,
            Values = values,
            SampleCount = ordered.Length,
            HistorySpanHours = spanHours
        };
        return true;
    }

    private static double CalculateSlopePerHour(IReadOnlyList<AssetHealthScorePoint> points)
    {
        var anchor = points[0].RecordedAtUtc;
        var x = points.Select(point => (point.RecordedAtUtc - anchor).TotalHours).ToArray();
        var y = points.Select(static point => point.HealthScore).ToArray();
        var xMean = x.Average();
        var yMean = y.Average();
        var denominator = x.Sum(value => Math.Pow(value - xMean, 2));
        if (denominator <= 1e-12) return 0;
        return x.Zip(y, (time, score) => (time - xMean) * (score - yMean)).Sum() / denominator;
    }
}

public static class AssetFailureForecastManifestValidator
{
    public const int CurrentSchemaVersion = 1;

    public static void Validate(
        AssetFailureForecastModelManifest manifest,
        AssetFailureForecastOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported failure forecast manifest schema: {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Forecast model Version is required.");
        if (!string.Equals(manifest.AdapterId, "microsoft.onnxruntime.cpu.forecast.v1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported forecast AdapterId: {manifest.AdapterId}.");
        if (string.IsNullOrWhiteSpace(manifest.ArtifactFile) || Path.IsPathRooted(manifest.ArtifactFile))
            throw new InvalidOperationException("Forecast ArtifactFile must be a relative local file name.");
        if (manifest.ArtifactFile.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Forecast ArtifactFile cannot traverse directories.");
        if (!IsSha256(manifest.ArtifactSha256))
            throw new InvalidOperationException("Forecast ArtifactSha256 must be a 64-character hexadecimal SHA-256 value.");
        if (manifest.CreatedUtc == default ||
            string.IsNullOrWhiteSpace(manifest.Source) ||
            string.IsNullOrWhiteSpace(manifest.ApprovedBy) ||
            manifest.ApprovedAtUtc is null)
            throw new InvalidOperationException("Forecast model source and approval metadata are required.");
        if (string.IsNullOrWhiteSpace(manifest.TrainingDatasetVersion))
            throw new InvalidOperationException("TrainingDatasetVersion is required.");
        if (manifest.TrainingAssetCount < options.MinimumTrainingAssets)
            throw new InvalidOperationException($"TrainingAssetCount must be at least {options.MinimumTrainingAssets}.");
        if (manifest.FailureEventCount < options.MinimumFailureEvents)
            throw new InvalidOperationException($"FailureEventCount must be at least {options.MinimumFailureEvents}.");
        if (manifest.CensoredRecordCount < 1)
            throw new InvalidOperationException("At least one censored training record is required for RUL governance.");
        if (!double.IsFinite(manifest.ValidationAuc) || manifest.ValidationAuc < options.MinimumValidationAuc || manifest.ValidationAuc > 1)
            throw new InvalidOperationException($"ValidationAuc must be between {options.MinimumValidationAuc} and 1.");
        if (!double.IsFinite(manifest.ValidationBrierScore) || manifest.ValidationBrierScore is < 0 || manifest.ValidationBrierScore > options.MaximumValidationBrierScore)
            throw new InvalidOperationException($"ValidationBrierScore must be between 0 and {options.MaximumValidationBrierScore}.");
        if (!double.IsFinite(manifest.ValidationRulMaeHours) || manifest.ValidationRulMaeHours < 0)
            throw new InvalidOperationException("ValidationRulMaeHours must be finite and non-negative.");
        if (!double.IsFinite(manifest.ValidationIntervalCoverage) ||
            manifest.ValidationIntervalCoverage < options.MinimumPredictionIntervalCoverage ||
            manifest.ValidationIntervalCoverage > 1)
            throw new InvalidOperationException(
                $"ValidationIntervalCoverage must be between {options.MinimumPredictionIntervalCoverage} and 1.");
        if (!manifest.FeatureNames.SequenceEqual(AssetFailureForecastFeatureSchema.Names, StringComparer.Ordinal))
            throw new InvalidOperationException("Forecast model FeatureNames must exactly match the governed feature schema.");
        if (manifest.Means.Length != manifest.FeatureNames.Length ||
            manifest.StandardDeviations.Length != manifest.FeatureNames.Length)
            throw new InvalidOperationException("Forecast normalization arrays must match FeatureNames length.");
        if (manifest.Means.Any(static value => !double.IsFinite(value)) ||
            manifest.StandardDeviations.Any(static value => !double.IsFinite(value) || value <= 0))
            throw new InvalidOperationException("Forecast normalization values must be finite and standard deviations positive.");
        if (string.IsNullOrWhiteSpace(manifest.InputName) || string.IsNullOrWhiteSpace(manifest.OutputName))
            throw new InvalidOperationException("Forecast input and output names are required.");
        if (manifest.InputShape.Length != 2 || manifest.InputShape[0] is not (-1 or 1) ||
            manifest.InputShape[1] != manifest.FeatureNames.Length)
            throw new InvalidOperationException("Forecast InputShape must be [-1|1, featureCount].");
        if (manifest.OutputShape.Length != 2 || manifest.OutputShape[0] is not (-1 or 1) || manifest.OutputShape[1] != 6)
            throw new InvalidOperationException("Forecast OutputShape must be [-1|1, 6].");
        if (!double.IsFinite(manifest.MaximumRulHours) || manifest.MaximumRulHours <= 0 || manifest.MaximumRulHours > 175_200)
            throw new InvalidOperationException("MaximumRulHours must be in (0, 175200].");
    }

    public static void VerifyArtifactHash(
        AssetFailureForecastModelManifest manifest,
        ReadOnlySpan<byte> content)
    {
        var actual = Convert.ToHexString(SHA256.HashData(content));
        if (!string.Equals(actual, manifest.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Failure forecast artifact hash mismatch. Expected={manifest.ArtifactSha256}; Actual={actual}.");
    }

    public static string ComputeManifestHash(AssetFailureForecastModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = string.Join('|',
            manifest.SchemaVersion,
            manifest.Version,
            manifest.AdapterId,
            manifest.ArtifactFile,
            manifest.ArtifactSha256.ToUpperInvariant(),
            manifest.CreatedUtc.ToUniversalTime().ToString("O"),
            manifest.Source,
            manifest.ApprovedBy,
            manifest.ApprovedAtUtc?.ToUniversalTime().ToString("O"),
            manifest.TrainingDatasetVersion,
            manifest.TrainingAssetCount,
            manifest.FailureEventCount,
            manifest.CensoredRecordCount,
            manifest.ValidationAuc.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            manifest.ValidationBrierScore.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            manifest.ValidationRulMaeHours.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            manifest.ValidationIntervalCoverage.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join('\u001f', manifest.FeatureNames),
            string.Join('\u001f', manifest.Means.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            string.Join('\u001f', manifest.StandardDeviations.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            manifest.InputName,
            manifest.OutputName,
            string.Join(',', manifest.InputShape),
            string.Join(',', manifest.OutputShape),
            manifest.MaximumRulHours.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static AssetFailureForecastOutput ValidateOutput(
        AssetFailureForecastOutput output,
        double maximumRulHours)
    {
        ArgumentNullException.ThrowIfNull(output);
        var probabilities = new[]
        {
            output.FailureProbability24Hours,
            output.FailureProbability72Hours,
            output.FailureProbability168Hours
        };
        if (probabilities.Any(static value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidOperationException("Failure probabilities must be finite and between 0 and 1.");
        if (probabilities[0] > probabilities[1] || probabilities[1] > probabilities[2])
            throw new InvalidOperationException("Failure probabilities must be monotonic across 24h, 72h and 168h horizons.");
        var rul = new[] { output.RulLowerHours, output.RulMedianHours, output.RulUpperHours };
        if (rul.Any(value => !double.IsFinite(value) || value < 0 || value > maximumRulHours))
            throw new InvalidOperationException("RUL values must be finite and inside the governed range.");
        if (rul[0] > rul[1] || rul[1] > rul[2])
            throw new InvalidOperationException("RUL interval must satisfy lower <= median <= upper.");
        return output;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character => Uri.IsHexDigit(character));
}
