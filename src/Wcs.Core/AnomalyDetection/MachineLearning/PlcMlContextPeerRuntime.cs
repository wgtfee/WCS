namespace Wcs.Core.AnomalyDetection.MachineLearning;

using Wcs.Core.AnomalyDetection;

public sealed record PlcMlContextPeerProfileStatus
{
    public required string ProfileId { get; init; }
    public bool Enabled { get; init; }
    public int TrackedContextDevices { get; init; }
    public long CompletedWindows { get; init; }
    public long DroppedIncompleteWindows { get; init; }
    public int TrackedWindows { get; init; }
    public required PlcMlPeerStatus Peer { get; init; }
}

public interface IPlcMlContextPeerRuntime
{
    ValueTask ProcessAsync(PlcAnomalySample sample, CancellationToken cancellationToken = default);
    Task MaintenanceAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    IReadOnlyList<PlcMlContextPeerProfileStatus> GetStatus();
}

/// <summary>
/// 上下文和同类设备对比使用独立窗口状态，不与 Isolation Forest 的训练/推理窗口共享可变状态。
/// </summary>
public sealed class PlcMlContextPeerRuntime : IPlcMlContextPeerRuntime
{
    private readonly PlcMlAnomalyOptions _options;
    private readonly PlcFeatureWindowEngine _windowEngine;
    private readonly PlcMlOperatingContextCenter _contextCenter;
    private readonly PlcMlPeerComparisonEngine _peerEngine;

    public PlcMlContextPeerRuntime(
        PlcMlAnomalyOptions options,
        PlcMlOperatingContextCenter contextCenter,
        PlcMlPeerComparisonEngine peerEngine)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contextCenter = contextCenter ?? throw new ArgumentNullException(nameof(contextCenter));
        _peerEngine = peerEngine ?? throw new ArgumentNullException(nameof(peerEngine));
        _windowEngine = new PlcFeatureWindowEngine(options);
    }

    public ValueTask ProcessAsync(
        PlcAnomalySample sample,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return ValueTask.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        _contextCenter.Update(sample);
        foreach (var vector in _windowEngine.Process(sample))
            AddWithContext(vector);
        return ValueTask.CompletedTask;
    }

    public async Task MaintenanceAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        foreach (var vector in _windowEngine.FlushExpired(utcNow))
            AddWithContext(vector);
        _contextCenter.Sweep(utcNow);
        await _peerEngine.FlushAsync(utcNow, cancellationToken);
    }

    public IReadOnlyList<PlcMlContextPeerProfileStatus> GetStatus() =>
        _options.Profiles.Select(profile =>
        {
            var windows = _windowEngine.GetMetrics(profile.ProfileId);
            return new PlcMlContextPeerProfileStatus
            {
                ProfileId = profile.ProfileId,
                Enabled = profile.Enabled && profile.PeerComparisonEnabled,
                TrackedContextDevices = _contextCenter.CountTracked(profile.ProfileId),
                CompletedWindows = windows.Completed,
                DroppedIncompleteWindows = windows.Dropped,
                TrackedWindows = windows.Tracked,
                Peer = _peerEngine.GetStatus(profile.ProfileId)
            };
        }).ToArray();

    private void AddWithContext(PlcFeatureVector vector)
    {
        var context = _contextCenter.Resolve(
            vector.ProfileId,
            vector.PlcName,
            vector.DeviceId,
            vector.WindowEndUtc);
        _peerEngine.Add(vector with { ContextKey = context });
    }
}
