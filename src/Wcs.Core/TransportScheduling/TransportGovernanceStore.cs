namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public sealed class InMemoryTransportGovernanceStore : ITransportGovernanceStore
{
    private readonly ConcurrentDictionary<string, TransportGovernedOperation> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<TransportAuditRecord> _audits = new();

    public Task SaveOperationAsync(TransportGovernedOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations[operation.OperationId] = operation;
        return Task.CompletedTask;
    }

    public Task<TransportGovernedOperation?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.TryGetValue(operationId, out var operation);
        return Task.FromResult(operation);
    }

    public Task<IReadOnlyList<TransportGovernedOperation>> GetOperationsAsync(int maxCount = 200, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportGovernedOperation> result = _operations.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Max(1, maxCount))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task AppendAuditAsync(TransportAuditRecord audit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audits.Enqueue(audit);
        while (_audits.Count > 5000 && _audits.TryDequeue(out _)) { }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportAuditRecord>> GetAuditsAsync(int maxCount = 500, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportAuditRecord> result = _audits
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Max(1, maxCount))
            .ToArray();
        return Task.FromResult(result);
    }
}
