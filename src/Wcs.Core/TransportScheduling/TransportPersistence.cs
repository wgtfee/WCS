namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public enum TransportCommandStatus
{
    Pending = 0,
    Sent = 1,
    Acknowledged = 2,
    Completed = 3,
    Failed = 4,
    TimedOut = 5
}

public sealed record TransportCommandRecord
{
    public string CommandId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportExecutionCommandType CommandType { get; init; }
    public string? TargetNodeId { get; init; }
    public TransportCommandStatus Status { get; init; } = TransportCommandStatus.Pending;
    public int RetryCount { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportRuntimeSnapshot
{
    public IReadOnlyList<TransportVehicleSnapshot> Vehicles { get; init; } = Array.Empty<TransportVehicleSnapshot>();
    public IReadOnlyList<TransportExecutionSnapshot> Executions { get; init; } = Array.Empty<TransportExecutionSnapshot>();
    public IReadOnlyList<RouteReservation> Reservations { get; init; } = Array.Empty<RouteReservation>();
    public IReadOnlyList<TransportCommandRecord> Commands { get; init; } = Array.Empty<TransportCommandRecord>();
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportStateStore
{
    Task SaveVehicleAsync(TransportVehicleSnapshot vehicle, CancellationToken cancellationToken = default);
    Task SaveExecutionAsync(TransportExecutionSnapshot execution, CancellationToken cancellationToken = default);
    Task SaveReservationAsync(RouteReservation reservation, CancellationToken cancellationToken = default);
    Task DeleteReservationAsync(string reservationId, CancellationToken cancellationToken = default);
    Task SaveCommandAsync(TransportCommandRecord command, CancellationToken cancellationToken = default);
    Task<TransportRuntimeSnapshot> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 第三阶段默认存储实现。生产环境可在 Infrastructure 中替换为 SqlSugar/SQL Server 实现。
/// </summary>
public sealed class InMemoryTransportStateStore : ITransportStateStore
{
    private readonly ConcurrentDictionary<string, TransportVehicleSnapshot> _vehicles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransportExecutionSnapshot> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteReservation> _reservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransportCommandRecord> _commands = new(StringComparer.Ordinal);

    public Task SaveVehicleAsync(TransportVehicleSnapshot vehicle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _vehicles[vehicle.VehicleId] = vehicle;
        return Task.CompletedTask;
    }

    public Task SaveExecutionAsync(TransportExecutionSnapshot execution, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _executions[execution.RequestId] = execution;
        return Task.CompletedTask;
    }

    public Task SaveReservationAsync(RouteReservation reservation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reservations[reservation.ReservationId] = reservation;
        return Task.CompletedTask;
    }

    public Task DeleteReservationAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reservations.TryRemove(reservationId, out _);
        return Task.CompletedTask;
    }

    public Task SaveCommandAsync(TransportCommandRecord command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _commands[command.CommandId] = command;
        return Task.CompletedTask;
    }

    public Task<TransportRuntimeSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TransportRuntimeSnapshot
        {
            Vehicles = _vehicles.Values.OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray(),
            Executions = _executions.Values.OrderByDescending(x => x.UpdatedAtUtc).ToArray(),
            Reservations = _reservations.Values.OrderBy(x => x.ExpiresAtUtc).ToArray(),
            Commands = _commands.Values.OrderByDescending(x => x.UpdatedAtUtc).ToArray()
        });
    }
}
