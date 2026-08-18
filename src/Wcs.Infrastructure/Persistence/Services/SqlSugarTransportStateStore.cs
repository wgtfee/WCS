namespace Wcs.Infrastructure.Persistence.Services;

using System.Text.Json;
using SqlSugar;
using Wcs.Core.TransportScheduling;
using Wcs.Infrastructure.Persistence;

public sealed class SqlSugarTransportStateStore : ITransportStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlSugarTransportStateStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task SaveVehicleAsync(TransportVehicleSnapshot vehicle, CancellationToken cancellationToken = default) =>
        UpsertAsync("Vehicle", vehicle.VehicleId, vehicle, vehicle.UpdatedAtUtc, cancellationToken);

    public Task SaveExecutionAsync(TransportExecutionSnapshot execution, CancellationToken cancellationToken = default) =>
        UpsertAsync("Execution", execution.RequestId, execution, execution.UpdatedAtUtc, cancellationToken);

    public Task SaveReservationAsync(RouteReservation reservation, CancellationToken cancellationToken = default) =>
        UpsertAsync("Reservation", reservation.ReservationId, reservation, reservation.CreatedAtUtc, cancellationToken);

    public Task DeleteReservationAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        db.Deleteable<TransportRuntimeStateEntity>()
            .Where(x => x.StateKey == Key("Reservation", reservationId))
            .ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task SaveCommandAsync(TransportCommandRecord command, CancellationToken cancellationToken = default) =>
        UpsertAsync("Command", command.CommandId, command, command.UpdatedAtUtc, cancellationToken);

    public Task<TransportRuntimeSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entities = db.Queryable<TransportRuntimeStateEntity>().ToList();

        var vehicles = Deserialize<TransportVehicleSnapshot>(entities, "Vehicle");
        var executions = Deserialize<TransportExecutionSnapshot>(entities, "Execution");
        var reservations = Deserialize<RouteReservation>(entities, "Reservation");
        var commands = Deserialize<TransportCommandRecord>(entities, "Command");

        return Task.FromResult(new TransportRuntimeSnapshot
        {
            Vehicles = vehicles.OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray(),
            Executions = executions.OrderByDescending(x => x.UpdatedAtUtc).ToArray(),
            Reservations = reservations.OrderBy(x => x.ExpiresAtUtc).ToArray(),
            Commands = commands.OrderByDescending(x => x.UpdatedAtUtc).ToArray()
        });
    }

    private Task UpsertAsync<T>(
        string category,
        string recordId,
        T value,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new TransportRuntimeStateEntity
        {
            StateKey = Key(category, recordId),
            Category = category,
            RecordId = recordId,
            PayloadJson = JsonSerializer.Serialize(value, JsonOptions),
            UpdatedAtUtc = updatedAtUtc
        };

        using var db = CreateClient();
        var exists = db.Queryable<TransportRuntimeStateEntity>()
            .Where(x => x.StateKey == entity.StateKey)
            .Any();
        if (exists)
            db.Updateable(entity).Where(x => x.StateKey == entity.StateKey).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    private static IReadOnlyList<T> Deserialize<T>(
        IEnumerable<TransportRuntimeStateEntity> entities,
        string category) =>
        entities
            .Where(x => string.Equals(x.Category, category, StringComparison.Ordinal))
            .Select(x => JsonSerializer.Deserialize<T>(x.PayloadJson, JsonOptions))
            .Where(x => x is not null)
            .Cast<T>()
            .ToArray();

    private static string Key(string category, string recordId) => $"{category}:{recordId}";

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}
