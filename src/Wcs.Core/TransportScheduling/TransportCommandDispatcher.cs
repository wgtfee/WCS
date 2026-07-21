namespace Wcs.Core.TransportScheduling;

public interface ITransportCommandDispatcher
{
    Task<TransportCommandRecord> DispatchAsync(
        TransportExecutionCommand command,
        TransportVehicleKind vehicleKind,
        int maxRetries = 3,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 第三阶段命令分发器：先持久化，再发送；每次状态变化都写回存储，避免重启后无法判断命令是否已下发。
/// </summary>
public sealed class TransportCommandDispatcher : ITransportCommandDispatcher
{
    private readonly ITransportDriverResolver _driverResolver;
    private readonly ITransportStateStore _stateStore;

    public TransportCommandDispatcher(
        ITransportDriverResolver driverResolver,
        ITransportStateStore stateStore)
    {
        _driverResolver = driverResolver;
        _stateStore = stateStore;
    }

    public async Task<TransportCommandRecord> DispatchAsync(
        TransportExecutionCommand command,
        TransportVehicleKind vehicleKind,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        var record = ToRecord(command, TransportCommandStatus.Pending, 0, null);
        await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);

        var driver = _driverResolver.Resolve(vehicleKind);
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record = record with
            {
                Status = TransportCommandStatus.Sent,
                RetryCount = attempt,
                UpdatedAtUtc = DateTime.UtcNow,
                Error = null
            };
            await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);

            try
            {
                var result = await driver.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                if (!result.Accepted)
                {
                    record = record with
                    {
                        Status = attempt == maxRetries ? TransportCommandStatus.Failed : TransportCommandStatus.Pending,
                        Error = result.Error ?? "设备拒绝命令",
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                record = record with
                {
                    Status = result.Completed ? TransportCommandStatus.Completed : TransportCommandStatus.Acknowledged,
                    Error = null,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);
                return record;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                record = record with
                {
                    Status = attempt == maxRetries ? TransportCommandStatus.TimedOut : TransportCommandStatus.Pending,
                    Error = "命令发送超时",
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                record = record with
                {
                    Status = attempt == maxRetries ? TransportCommandStatus.Failed : TransportCommandStatus.Pending,
                    Error = ex.Message,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await _stateStore.SaveCommandAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        return record;
    }

    private static TransportCommandRecord ToRecord(
        TransportExecutionCommand command,
        TransportCommandStatus status,
        int retryCount,
        string? error) => new()
    {
        CommandId = command.CommandId,
        RequestId = command.RequestId,
        VehicleId = command.VehicleId,
        CommandType = command.CommandType,
        TargetNodeId = command.TargetNodeId,
        Status = status,
        RetryCount = retryCount,
        Error = error,
        CreatedAtUtc = command.CreatedAtUtc,
        UpdatedAtUtc = DateTime.UtcNow
    };
}
