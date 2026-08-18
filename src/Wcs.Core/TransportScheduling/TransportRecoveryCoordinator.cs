namespace Wcs.Core.TransportScheduling;

public enum TransportRecoveryDecision
{
    RestoredPaused = 0,
    RequiresManualConfirmation = 1,
    VehicleOffline = 2,
    PositionMismatch = 3,
    SkippedTerminal = 4
}

public sealed record TransportRecoveryItem
{
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportRecoveryDecision Decision { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportRecoveryReport
{
    public IReadOnlyList<TransportRecoveryItem> Items { get; init; } = Array.Empty<TransportRecoveryItem>();
    public DateTime RecoveredAtUtc { get; init; } = DateTime.UtcNow;
    public int RestoredCount => Items.Count(x => x.Decision == TransportRecoveryDecision.RestoredPaused);
    public int ManualConfirmationCount => Items.Count(x => x.Decision == TransportRecoveryDecision.RequiresManualConfirmation || x.Decision == TransportRecoveryDecision.PositionMismatch);
}

public interface ITransportRecoveryCoordinator
{
    Task<TransportRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 重启恢复采用安全优先策略：只恢复内存索引，不自动继续运动；
/// 车辆离线或实际位置与持久化位置不一致时，要求人工确认。
/// </summary>
public sealed class TransportRecoveryCoordinator : ITransportRecoveryCoordinator
{
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportDriverResolver _driverResolver;

    public TransportRecoveryCoordinator(
        ITransportStateStore stateStore,
        ITransportDriverResolver driverResolver)
    {
        _stateStore = stateStore;
        _driverResolver = driverResolver;
    }

    public async Task<TransportRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var vehicleKinds = snapshot.Vehicles.ToDictionary(x => x.VehicleId, x => x.Kind, StringComparer.Ordinal);
        var items = new List<TransportRecoveryItem>();

        foreach (var execution in snapshot.Executions.OrderBy(x => x.CreatedAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (execution.IsTerminal)
            {
                items.Add(new TransportRecoveryItem
                {
                    RequestId = execution.RequestId,
                    VehicleId = execution.VehicleId,
                    Decision = TransportRecoveryDecision.SkippedTerminal,
                    Message = "终态任务无需恢复"
                });
                continue;
            }

            if (!vehicleKinds.TryGetValue(execution.VehicleId, out var kind))
            {
                items.Add(new TransportRecoveryItem
                {
                    RequestId = execution.RequestId,
                    VehicleId = execution.VehicleId,
                    Decision = TransportRecoveryDecision.RequiresManualConfirmation,
                    Message = "持久化车辆信息缺失"
                });
                continue;
            }

            var driverState = await _driverResolver.Resolve(kind)
                .ReadStateAsync(execution.VehicleId, cancellationToken)
                .ConfigureAwait(false);

            if (!driverState.IsOnline)
            {
                items.Add(new TransportRecoveryItem
                {
                    RequestId = execution.RequestId,
                    VehicleId = execution.VehicleId,
                    Decision = TransportRecoveryDecision.VehicleOffline,
                    Message = "车辆离线，禁止自动恢复"
                });
                continue;
            }

            if (!string.Equals(driverState.CurrentNodeId, execution.CurrentNodeId, StringComparison.Ordinal))
            {
                items.Add(new TransportRecoveryItem
                {
                    RequestId = execution.RequestId,
                    VehicleId = execution.VehicleId,
                    Decision = TransportRecoveryDecision.PositionMismatch,
                    Message = $"位置不一致：数据库={execution.CurrentNodeId}，设备={driverState.CurrentNodeId}"
                });
                continue;
            }

            items.Add(new TransportRecoveryItem
            {
                RequestId = execution.RequestId,
                VehicleId = execution.VehicleId,
                Decision = TransportRecoveryDecision.RestoredPaused,
                Message = "状态一致，恢复为暂停态，等待人工或上层系统确认继续"
            });
        }

        return new TransportRecoveryReport { Items = items };
    }
}
