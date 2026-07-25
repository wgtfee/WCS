namespace Wcs.Core.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.IO.Enumeration;
using Wcs.Core.AnomalyDetection;

/// <summary>
/// 将原始 PLC 样本聚合为固定窗口特征。只保存在线统计量，不保存窗口内全部原始点。
/// </summary>
public sealed class PlcFeatureWindowEngine
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProfileMetrics> _metrics = new(StringComparer.Ordinal);

    public PlcFeatureWindowEngine(PlcMlAnomalyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        foreach (var profile in options.Profiles)
            _metrics.TryAdd(profile.ProfileId, new ProfileMetrics());
    }

    public IReadOnlyList<PlcFeatureVector> Process(PlcAnomalySample sample)
    {
        if (!_options.Enabled) return Array.Empty<PlcFeatureVector>();
        List<PlcFeatureVector>? completed = null;

        foreach (var profile in _options.Profiles)
        {
            if (!ProfileMatches(profile, sample)) continue;
            var matchingSignals = FindMatchingSignalIndices(profile, sample.SignalName);
            if (matchingSignals.Count == 0) continue;

            var key = BuildWindowKey(profile.ProfileId, sample.PlcName, sample.DeviceId);
            if (!_windows.TryGetValue(key, out var state))
            {
                if (_windows.Count >= _options.MaximumTrackedWindows)
                {
                    _metrics[profile.ProfileId].IncrementDropped();
                    continue;
                }

                state = _windows.GetOrAdd(
                    key,
                    _ => WindowState.Create(profile, sample.PlcName, sample.DeviceId, sample.TimestampUtc));
            }

            PlcFeatureVector? vector = null;
            lock (state.Gate)
            {
                if (sample.TimestampUtc < state.WindowStartUtc)
                    continue;

                if (sample.TimestampUtc >= state.WindowEndUtc)
                {
                    vector = TryBuildVector(state);
                    if (vector is null) _metrics[profile.ProfileId].IncrementDropped();
                    else _metrics[profile.ProfileId].IncrementCompleted();
                    state.Reset(sample.TimestampUtc);
                }

                foreach (var signalIndex in matchingSignals)
                    state.Accumulators[signalIndex].Add(sample);
                state.LastUpdatedUtc = sample.TimestampUtc;
            }

            if (vector is not null)
            {
                completed ??= new List<PlcFeatureVector>();
                completed.Add(vector);
            }
        }

        return completed ?? Array.Empty<PlcFeatureVector>();
    }

    public IReadOnlyList<PlcFeatureVector> FlushExpired(DateTime utcNow)
    {
        List<PlcFeatureVector>? completed = null;
        foreach (var pair in _windows)
        {
            var state = pair.Value;
            if (utcNow < state.WindowEndUtc) continue;

            PlcFeatureVector? vector;
            lock (state.Gate)
            {
                if (utcNow < state.WindowEndUtc) continue;
                vector = TryBuildVector(state);
            }

            if (!_windows.TryRemove(new KeyValuePair<string, WindowState>(pair.Key, state))) continue;
            if (vector is null)
            {
                _metrics[state.Profile.ProfileId].IncrementDropped();
                continue;
            }

            _metrics[state.Profile.ProfileId].IncrementCompleted();
            completed ??= new List<PlcFeatureVector>();
            completed.Add(vector);
        }
        return completed ?? Array.Empty<PlcFeatureVector>();
    }

    public WindowMetricsSnapshot GetMetrics(string profileId)
    {
        _metrics.TryGetValue(profileId, out var metrics);
        var tracked = _windows.Values.Count(item =>
            string.Equals(item.Profile.ProfileId, profileId, StringComparison.Ordinal));
        return new WindowMetricsSnapshot(
            metrics?.Completed ?? 0,
            metrics?.Dropped ?? 0,
            tracked);
    }

    private static PlcFeatureVector? TryBuildVector(WindowState state)
    {
        if (state.Accumulators.Any(accumulator =>
                accumulator.Count < state.Profile.MinimumSamplesPerSignal))
            return null;

        var names = new List<string>(state.Accumulators.Length * 8);
        var values = new List<double>(state.Accumulators.Length * 8);
        var sourceSamples = 0;
        for (var index = 0; index < state.Accumulators.Length; index++)
        {
            var definition = state.Profile.Signals[index];
            var accumulator = state.Accumulators[index];
            sourceSamples += accumulator.Count;
            accumulator.AppendFeatures(definition, state.WindowStartUtc, state.WindowEndUtc, names, values);
        }

        return new PlcFeatureVector
        {
            ProfileId = state.Profile.ProfileId,
            PlcName = state.PlcName,
            DeviceId = state.DeviceId,
            WindowStartUtc = state.WindowStartUtc,
            WindowEndUtc = state.WindowEndUtc,
            FeatureNames = names.ToArray(),
            Values = values.ToArray(),
            SourceSampleCount = sourceSamples
        };
    }

    private static List<int> FindMatchingSignalIndices(PlcMlProfile profile, string signalName)
    {
        var result = new List<int>(1);
        for (var index = 0; index < profile.Signals.Count; index++)
        {
            if (WildcardMatch(profile.Signals[index].Pattern, signalName)) result.Add(index);
        }
        return result;
    }

    private static bool ProfileMatches(PlcMlProfile profile, PlcAnomalySample sample) =>
        profile.Enabled &&
        profile.Signals.Count > 0 &&
        WildcardMatch(profile.PlcPattern, sample.PlcName) &&
        WildcardMatch(profile.DevicePattern, sample.DeviceId);

    private static bool WildcardMatch(string? pattern, string value) =>
        string.IsNullOrWhiteSpace(pattern) || pattern == "*" ||
        FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);

    private static string BuildWindowKey(string profileId, string plcName, string deviceId) =>
        $"{profileId}|{plcName}|{deviceId}";

    private sealed class WindowState
    {
        private WindowState(
            PlcMlProfile profile,
            string plcName,
            string deviceId,
            DateTime startUtc)
        {
            Profile = profile;
            PlcName = plcName;
            DeviceId = deviceId;
            Accumulators = profile.Signals.Select(static _ => new SignalAccumulator()).ToArray();
            SetWindow(startUtc);
        }

        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public string PlcName { get; }
        public string DeviceId { get; }
        public SignalAccumulator[] Accumulators { get; }
        public DateTime WindowStartUtc { get; private set; }
        public DateTime WindowEndUtc { get; private set; }
        public DateTime LastUpdatedUtc { get; set; }

        public static WindowState Create(
            PlcMlProfile profile,
            string plcName,
            string deviceId,
            DateTime timestampUtc) => new(profile, plcName, deviceId, timestampUtc);

        public void Reset(DateTime timestampUtc)
        {
            foreach (var accumulator in Accumulators) accumulator.Reset();
            SetWindow(timestampUtc);
        }

        private void SetWindow(DateTime timestampUtc)
        {
            var durationTicks = TimeSpan.FromSeconds(Profile.WindowSeconds).Ticks;
            var ticks = timestampUtc.Ticks - timestampUtc.Ticks % durationTicks;
            WindowStartUtc = new DateTime(ticks, DateTimeKind.Utc);
            WindowEndUtc = WindowStartUtc.AddTicks(durationTicks);
            LastUpdatedUtc = timestampUtc;
        }
    }

    private sealed class SignalAccumulator
    {
        private double _sum;
        private double _sumSquares;
        private double _minimum;
        private double _maximum;
        private double _firstNumeric;
        private double _lastNumeric;
        private DateTime _firstNumericUtc;
        private DateTime _lastNumericUtc;
        private bool _lastBoolean;
        private bool _hasBoolean;
        private int _trueCount;
        private int _transitions;

        public int Count { get; private set; }

        public void Add(PlcAnomalySample sample)
        {
            if (sample.NumericValue is { } numeric)
            {
                if (Count == 0)
                {
                    _minimum = _maximum = _firstNumeric = numeric;
                    _firstNumericUtc = sample.TimestampUtc;
                }
                _minimum = Math.Min(_minimum, numeric);
                _maximum = Math.Max(_maximum, numeric);
                _lastNumeric = numeric;
                _lastNumericUtc = sample.TimestampUtc;
                _sum += numeric;
                _sumSquares += numeric * numeric;
                Count++;
                return;
            }

            if (sample.BooleanValue is not { } boolean) return;
            if (_hasBoolean && boolean != _lastBoolean) _transitions++;
            _hasBoolean = true;
            _lastBoolean = boolean;
            if (boolean) _trueCount++;
            Count++;
        }

        public void AppendFeatures(
            PlcMlSignalDefinition definition,
            DateTime windowStartUtc,
            DateTime windowEndUtc,
            ICollection<string> names,
            ICollection<double> values)
        {
            var prefix = definition.Name;
            if (definition.Kind == PlcMlSignalKind.Numeric)
            {
                var mean = _sum / Count;
                var variance = Math.Max(0, _sumSquares / Count - mean * mean);
                var elapsed = Math.Max((_lastNumericUtc - _firstNumericUtc).TotalSeconds, 1e-9);
                names.Add($"{prefix}.mean"); values.Add(mean);
                names.Add($"{prefix}.stddev"); values.Add(Math.Sqrt(variance));
                names.Add($"{prefix}.min"); values.Add(_minimum);
                names.Add($"{prefix}.max"); values.Add(_maximum);
                names.Add($"{prefix}.last"); values.Add(_lastNumeric);
                names.Add($"{prefix}.slope"); values.Add((_lastNumeric - _firstNumeric) / elapsed);
                names.Add($"{prefix}.range"); values.Add(_maximum - _minimum);
                names.Add($"{prefix}.samplesPerSecond");
                values.Add(Count / Math.Max((windowEndUtc - windowStartUtc).TotalSeconds, 1e-9));
                return;
            }

            names.Add($"{prefix}.trueRatio"); values.Add((double)_trueCount / Count);
            names.Add($"{prefix}.transitions"); values.Add(_transitions);
            names.Add($"{prefix}.last"); values.Add(_lastBoolean ? 1.0 : 0.0);
            names.Add($"{prefix}.samplesPerSecond");
            values.Add(Count / Math.Max((windowEndUtc - windowStartUtc).TotalSeconds, 1e-9));
        }

        public void Reset()
        {
            _sum = 0;
            _sumSquares = 0;
            _minimum = 0;
            _maximum = 0;
            _firstNumeric = 0;
            _lastNumeric = 0;
            _firstNumericUtc = default;
            _lastNumericUtc = default;
            _lastBoolean = false;
            _hasBoolean = false;
            _trueCount = 0;
            _transitions = 0;
            Count = 0;
        }
    }

    private sealed class ProfileMetrics
    {
        private long _completed;
        private long _dropped;
        public long Completed => Interlocked.Read(ref _completed);
        public long Dropped => Interlocked.Read(ref _dropped);
        public void IncrementCompleted() => Interlocked.Increment(ref _completed);
        public void IncrementDropped() => Interlocked.Increment(ref _dropped);
    }
}

public sealed record WindowMetricsSnapshot(long Completed, long Dropped, int Tracked);
