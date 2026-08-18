namespace Wcs.Infrastructure.Persistence.Services;

using System.Text.Json;
using SqlSugar;
using Wcs.Core.TransportScheduling;

public sealed class SqlSugarTransportGovernanceStore : ITransportGovernanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlSugarTransportGovernanceStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task SaveOperationAsync(TransportGovernedOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = new TransportGovernedOperationEntity
        {
            OperationId = operation.OperationId,
            OperationType = (int)operation.OperationType,
            State = (int)operation.State,
            TargetId = operation.TargetId,
            RequestedBy = operation.RequestedBy,
            PayloadJson = JsonSerializer.Serialize(operation, JsonOptions),
            RequestedAtUtc = operation.RequestedAtUtc,
            ExpiresAtUtc = operation.ExpiresAtUtc,
            UpdatedAtUtc = operation.UpdatedAtUtc
        };

        if (db.Queryable<TransportGovernedOperationEntity>().Any(x => x.OperationId == operation.OperationId))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();

        return Task.CompletedTask;
    }

    public Task<TransportGovernedOperation?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = db.Queryable<TransportGovernedOperationEntity>()
            .Where(x => x.OperationId == operationId)
            .First();
        return Task.FromResult(entity is null
            ? null
            : JsonSerializer.Deserialize<TransportGovernedOperation>(entity.PayloadJson, JsonOptions));
    }

    public Task<IReadOnlyList<TransportGovernedOperation>> GetOperationsAsync(int maxCount = 200, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<TransportGovernedOperation> result = db.Queryable<TransportGovernedOperationEntity>()
            .OrderBy(x => x.UpdatedAtUtc, OrderByType.Desc)
            .Take(Math.Max(1, maxCount))
            .ToList()
            .Select(x => JsonSerializer.Deserialize<TransportGovernedOperation>(x.PayloadJson, JsonOptions))
            .Where(x => x is not null)
            .Cast<TransportGovernedOperation>()
            .ToArray();
        return Task.FromResult(result);
    }

    public Task AppendAuditAsync(TransportAuditRecord audit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        db.Insertable(new TransportAuditEntity
        {
            AuditId = audit.AuditId,
            OperationId = audit.OperationId,
            Action = audit.Action,
            ActorId = audit.ActorId,
            TargetId = audit.TargetId,
            PayloadJson = JsonSerializer.Serialize(audit, JsonOptions),
            Success = audit.Success,
            OccurredAtUtc = audit.OccurredAtUtc
        }).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportAuditRecord>> GetAuditsAsync(int maxCount = 500, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<TransportAuditRecord> result = db.Queryable<TransportAuditEntity>()
            .OrderBy(x => x.OccurredAtUtc, OrderByType.Desc)
            .Take(Math.Max(1, maxCount))
            .ToList()
            .Select(x => string.IsNullOrWhiteSpace(x.PayloadJson)
                ? null
                : JsonSerializer.Deserialize<TransportAuditRecord>(x.PayloadJson, JsonOptions))
            .Where(x => x is not null)
            .Cast<TransportAuditRecord>()
            .ToArray();
        return Task.FromResult(result);
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}
