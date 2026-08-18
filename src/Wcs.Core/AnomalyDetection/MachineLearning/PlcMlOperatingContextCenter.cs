namespace Wcs.Core.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.IO.Enumeration;
using Wcs.Core.AnomalyDetection;

/// <summary>
/// 维护设备运行上下文。上下文只用于分组，不进入 Isolation Forest 特征，
/// 因此不会改变既有模型的特征名称和顺序。
/// </summary>
public sealed class PlcMlOperatingContextCenter
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly IReadOnlyDictionary<string, PlcMlProfile> _profiles;
    private readonly ConcurrentDictionary<string, ContextState> _states = new(StringComparer.Ordinal);

    public PlcMlOperatingContextCenter(PlcMlAnomalyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profiles = options.Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);
    }

    public void Update(PlcAnomalySample sample)
    {
        if (!_options.Enabled) return;
        foreach (var profile in _options.Profiles)
        {
            if (!ProfileMatches(profile, sample) || profile.ContextSignals.Count == 0) continue;
            foreach (var definition in profile.ContextSignals)
            {
                if (!WildcardMatch(definition.Pattern, sample.SignalName)) continue;
                var value = NormalizeContextValue(sample.NewValue, definition.DefaultValue);
                var key = BuildKey(profile.ProfileId, sample.PlcName, sample.DeviceId);
                var state = _states.GetOrAdd(key, _ => new ContextState(profile.ProfileId, sample.PlcName, sample.DeviceId));
                lock (state.Gate)
                {
                    state.Values[definition.Name] = new ContextValue(value, sample.TimestampUtc);
                    state.LastUpdatedUtc = sample.TimestampUtc;
                }
            }
        }
    }

    public string Resolve(
        string profileId,
        string plcName,
        string deviceId,
        DateTime utcNow)
    {
        if (!_profiles.TryGetValue(profileId, out var profile) || profile.ContextSignals.Count == 0)
            return "default";

        var key = BuildKey(profileId, plcName, deviceId);
        _states.TryGetValue(key, out var state);
        var parts = new string[profile.ContextSignals.Count];
        if (state is null)
        {
            for (var index = 0; index < profile.ContextSignals.Count; index++)
            {
                var definition = profile.ContextSignals[index];
                parts[index] = $"{Escape(definition.Name)}={Escape(definition.DefaultValue)}";
            }
            return string.Join('|', parts);
        }

        lock (state.Gate)
        {
            for (var index = 0; index < profile.ContextSignals.Count; index++)
            {
                var definition = profile.ContextSignals[index];
                var value = definition.DefaultValue;
                if (state.Values.TryGetValue(definition.Name, out var current) &&
                    utcNow - current.TimestampUtc <= TimeSpan.FromSeconds(definition.MaximumAgeSeconds))
                    value = current.Value;
                parts[index] = $"{Escape(definition.Name)}={Escape(value)}";
            }
        }
        return string.Join('|', parts);
    }

    public void Sweep(DateTime utcNow)
    {
        foreach (var pair in _states)
        {
            if (!_profiles.TryGetValue(pair.Value.ProfileId, out var profile))
            {
                ((ICollection<KeyValuePair<string, ContextState>>)_states).Remove(pair);
                continue;
            }

            var maximumAge = profile.ContextSignals.Count == 0
                ? 60
                : profile.ContextSignals.Max(definition => definition.MaximumAgeSeconds) + 60;
            if (utcNow - pair.Value.LastUpdatedUtc <= TimeSpan.FromSeconds(maximumAge)) continue;
            ((ICollection<KeyValuePair<string, ContextState>>)_states).Remove(pair);
        }
    }

    public int CountTracked(string profileId) =>
        _states.Values.Count(state => string.Equals(state.ProfileId, profileId, StringComparison.Ordinal));

    private static bool ProfileMatches(PlcMlProfile profile, PlcAnomalySample sample) =>
        profile.Enabled &&
        WildcardMatch(profile.PlcPattern, sample.PlcName) &&
        WildcardMatch(profile.DevicePattern, sample.DeviceId);

    private static bool WildcardMatch(string? pattern, string value) =>
        string.IsNullOrWhiteSpace(pattern) || pattern == "*" ||
        FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);

    private static string NormalizeContextValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback.Trim() : value.Trim();

    private static string BuildKey(string profileId, string plcName, string deviceId) =>
        $"{profileId}|{plcName}|{deviceId}";

    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal);

    private sealed class ContextState
    {
        public ContextState(string profileId, string plcName, string deviceId)
        {
            ProfileId = profileId;
            PlcName = plcName;
            DeviceId = deviceId;
        }

        public object Gate { get; } = new();
        public string ProfileId { get; }
        public string PlcName { get; }
        public string DeviceId { get; }
        public Dictionary<string, ContextValue> Values { get; } = new(StringComparer.Ordinal);
        public DateTime LastUpdatedUtc { get; set; }
    }

    private sealed record ContextValue(string Value, DateTime TimestampUtc);
}
