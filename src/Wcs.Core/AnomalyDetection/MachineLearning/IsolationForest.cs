namespace Wcs.Core.AnomalyDetection.MachineLearning;

/// <summary>
/// 纯 .NET Isolation Forest。训练阶段使用确定性随机种子；森林与阈值校准使用互斥数据集，
/// 避免在训练样本自身上计算阈值导致未见正常数据误报偏高。训练窗口先稳定排序，
/// 因此并发采集文件的写入顺序不会改变模型结果。
/// </summary>
public static class IsolationForest
{
    public static PlcIsolationForestModel Train(
        PlcMlProfile profile,
        IReadOnlyList<PlcFeatureVector> vectors,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count < Math.Max(20, profile.MinimumTrainingWindows))
            throw new InvalidOperationException(
                $"Profile {profile.ProfileId} 训练窗口不足：{vectors.Count}/{Math.Max(20, profile.MinimumTrainingWindows)}。");

        var ordered = vectors
            .OrderBy(static vector => vector.PlcName, StringComparer.Ordinal)
            .ThenBy(static vector => vector.DeviceId, StringComparer.Ordinal)
            .ThenBy(static vector => vector.WindowStartUtc)
            .ThenBy(static vector => vector.WindowEndUtc)
            .ToArray();
        var featureNames = ordered[0].FeatureNames;
        if (featureNames.Length == 0)
            throw new InvalidOperationException("训练特征不能为空。");

        foreach (var vector in ordered)
        {
            if (vector.Values.Length != featureNames.Length ||
                !vector.FeatureNames.SequenceEqual(featureNames, StringComparer.Ordinal))
                throw new InvalidOperationException("同一模型的训练特征维度或顺序不一致。");
            if (vector.Values.Any(static value => !double.IsFinite(value)))
                throw new InvalidOperationException("训练特征包含 NaN 或 Infinity。");
        }

        var shuffled = Enumerable.Range(0, ordered.Length).ToArray();
        Shuffle(shuffled, new Random(unchecked(profile.RandomSeed ^ 0x5F3759DF)));
        var calibrationCount = Math.Max(5, ordered.Length / 5);
        calibrationCount = Math.Min(calibrationCount, ordered.Length - 2);
        var calibrationIndices = shuffled[..calibrationCount];
        var forestIndices = shuffled[calibrationCount..];

        var means = new double[featureNames.Length];
        var standardDeviations = new double[featureNames.Length];
        for (var feature = 0; feature < featureNames.Length; feature++)
        {
            var sum = 0.0;
            foreach (var index in forestIndices) sum += ordered[index].Values[feature];
            var mean = sum / forestIndices.Length;
            means[feature] = mean;

            var variance = 0.0;
            foreach (var index in forestIndices)
            {
                var delta = ordered[index].Values[feature] - mean;
                variance += delta * delta;
            }
            standardDeviations[feature] = Math.Max(
                Math.Sqrt(variance / forestIndices.Length),
                1e-9);
        }

        var forestRows = forestIndices
            .Select(index => Normalize(ordered[index].Values, means, standardDeviations))
            .ToArray();
        var calibrationRows = calibrationIndices
            .Select(index => Normalize(ordered[index].Values, means, standardDeviations))
            .ToArray();
        var sampleSize = Math.Clamp(profile.SampleSize, 2, forestRows.Length);
        var treeCount = Math.Clamp(profile.TreeCount, 1, 1_000);
        var depthLimit = Math.Max(1, (int)Math.Ceiling(Math.Log2(sampleSize)));
        var trees = new IsolationForestNode[treeCount];

        for (var treeIndex = 0; treeIndex < treeCount; treeIndex++)
        {
            var random = new Random(unchecked(profile.RandomSeed + treeIndex * 104_729));
            var sampleIndices = SampleWithoutReplacement(forestRows.Length, sampleSize, random);
            trees[treeIndex] = BuildTree(forestRows, sampleIndices, 0, depthLimit, random);
        }

        var provisional = new PlcIsolationForestModel
        {
            ProfileId = profile.ProfileId,
            Version = $"{utcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25],
            CreatedUtc = utcNow,
            FeatureNames = featureNames.ToArray(),
            Means = means,
            StandardDeviations = standardDeviations,
            Trees = trees,
            TrainingSampleCount = ordered.Length,
            CalibrationSampleCount = calibrationRows.Length,
            SubsampleSize = sampleSize,
            Contamination = Math.Clamp(profile.Contamination, 0.0001, 0.49)
        };

        var scores = calibrationRows
            .Select(values => ScoreNormalized(provisional, values))
            .OrderBy(static score => score)
            .ToArray();
        var quantileIndex = Math.Clamp(
            (int)Math.Ceiling((1.0 - provisional.Contamination) * scores.Length) - 1,
            0,
            scores.Length - 1);
        var p95Index = Math.Clamp(
            (int)Math.Ceiling(0.95 * scores.Length) - 1,
            0,
            scores.Length - 1);
        provisional.DecisionThreshold = scores[quantileIndex];
        provisional.CalibrationMeanScore = scores.Average();
        provisional.CalibrationP95Score = scores[p95Index];
        return provisional;
    }

    public static double Score(PlcIsolationForestModel model, double[] values)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != model.FeatureNames.Length)
            throw new ArgumentException("推理特征维度与模型不一致。", nameof(values));
        if (values.Any(static value => !double.IsFinite(value)))
            throw new ArgumentException("推理特征包含 NaN 或 Infinity。", nameof(values));
        return ScoreNormalized(model, Normalize(values, model.Means, model.StandardDeviations));
    }

    public static double[] Normalize(double[] values, double[] means, double[] standardDeviations)
    {
        if (values.Length != means.Length || values.Length != standardDeviations.Length)
            throw new ArgumentException("标准化数组维度不一致。");
        var normalized = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
            normalized[index] = (values[index] - means[index]) / Math.Max(standardDeviations[index], 1e-9);
        return normalized;
    }

    private static double ScoreNormalized(PlcIsolationForestModel model, double[] normalized)
    {
        if (model.Trees.Length == 0) return 0;
        var pathSum = 0.0;
        foreach (var tree in model.Trees)
            pathSum += PathLength(tree, normalized, 0);
        var averagePath = pathSum / model.Trees.Length;
        var normalizer = AveragePathLength(Math.Max(2, model.SubsampleSize));
        return Math.Pow(2.0, -averagePath / Math.Max(normalizer, 1e-9));
    }

    private static IsolationForestNode BuildTree(
        double[][] rows,
        int[] indices,
        int depth,
        int depthLimit,
        Random random)
    {
        if (indices.Length <= 1 || depth >= depthLimit)
            return new IsolationForestNode { SampleCount = indices.Length };

        var candidateFeatures = new List<(int Feature, double Min, double Max)>();
        for (var feature = 0; feature < rows[0].Length; feature++)
        {
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            foreach (var index in indices)
            {
                var value = rows[index][feature];
                if (value < min) min = value;
                if (value > max) max = value;
            }
            if (max - min > 1e-12) candidateFeatures.Add((feature, min, max));
        }

        if (candidateFeatures.Count == 0)
            return new IsolationForestNode { SampleCount = indices.Length };

        var selected = candidateFeatures[random.Next(candidateFeatures.Count)];
        var split = selected.Min + random.NextDouble() * (selected.Max - selected.Min);
        var left = indices.Where(index => rows[index][selected.Feature] < split).ToArray();
        var right = indices.Where(index => rows[index][selected.Feature] >= split).ToArray();
        if (left.Length == 0 || right.Length == 0)
            return new IsolationForestNode { SampleCount = indices.Length };

        return new IsolationForestNode
        {
            FeatureIndex = selected.Feature,
            SplitValue = split,
            SampleCount = indices.Length,
            Left = BuildTree(rows, left, depth + 1, depthLimit, random),
            Right = BuildTree(rows, right, depth + 1, depthLimit, random)
        };
    }

    private static double PathLength(IsolationForestNode node, double[] values, int depth)
    {
        if (node.IsLeaf)
            return depth + AveragePathLength(node.SampleCount);
        var next = values[node.FeatureIndex] < node.SplitValue ? node.Left : node.Right;
        return next is null ? depth : PathLength(next, values, depth + 1);
    }

    private static int[] SampleWithoutReplacement(int population, int count, Random random)
    {
        var values = Enumerable.Range(0, population).ToArray();
        for (var index = 0; index < count; index++)
        {
            var swapIndex = random.Next(index, population);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
        return values[..count];
    }

    private static void Shuffle(int[] values, Random random)
    {
        for (var index = 0; index < values.Length - 1; index++)
        {
            var swapIndex = random.Next(index, values.Length);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }

    private static double AveragePathLength(int sampleCount)
    {
        if (sampleCount <= 1) return 0;
        if (sampleCount == 2) return 1;
        var n = sampleCount - 1.0;
        var harmonic = Math.Log(n) + 0.5772156649015329 + 1.0 / (2.0 * n) - 1.0 / (12.0 * n * n);
        return 2.0 * harmonic - 2.0 * (sampleCount - 1.0) / sampleCount;
    }
}
