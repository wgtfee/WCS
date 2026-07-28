namespace Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

using Wcs.Core.AnomalyDetection.MachineLearning;

public static class PlcMlFeatureSchema
{
    public static string[] BuildExpectedFeatureNames(PlcMlProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = new List<string>(profile.Signals.Count * 8);
        foreach (var signal in profile.Signals)
        {
            if (string.IsNullOrWhiteSpace(signal.Name))
                throw new InvalidOperationException("PLC ML signal name is required to build a feature schema.");
            if (signal.Kind == PlcMlSignalKind.Numeric)
            {
                result.Add($"{signal.Name}.mean");
                result.Add($"{signal.Name}.stddev");
                result.Add($"{signal.Name}.min");
                result.Add($"{signal.Name}.max");
                result.Add($"{signal.Name}.last");
                result.Add($"{signal.Name}.slope");
                result.Add($"{signal.Name}.range");
                result.Add($"{signal.Name}.samplesPerSecond");
            }
            else
            {
                result.Add($"{signal.Name}.trueRatio");
                result.Add($"{signal.Name}.transitions");
                result.Add($"{signal.Name}.last");
                result.Add($"{signal.Name}.samplesPerSecond");
            }
        }
        return result.ToArray();
    }

    public static void ValidateManifest(PlcMlProfile profile, PlcMlModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(manifest);
        var expected = BuildExpectedFeatureNames(profile);
        if (!manifest.FeatureNames.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Manifest features do not match Profile {profile.ProfileId}. Expected={string.Join(',', expected)}; Actual={string.Join(',', manifest.FeatureNames)}.");
    }
}
