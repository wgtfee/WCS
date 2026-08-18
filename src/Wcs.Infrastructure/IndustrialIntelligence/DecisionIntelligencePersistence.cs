namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcs.DecisionIntelligence;

public sealed class DecisionIntelligencePersistenceFactory
{
    private static readonly object SchemaSync = new();
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public DecisionIntelligencePersistenceFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        lock (SchemaSync)
        {
            if (InitializedConnectionStrings.Contains(_connectionString)) return;
            using var db = CreateDb();
            DecisionIntelligenceSchema.Ensure(db);
            InitializedConnectionStrings.Add(_connectionString);
        }
    }

    public SqlDecisionProposalStore CreateStore() => new(_connectionString);

    private SqlSugarClient CreateDb() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}

public sealed class SqlDecisionProposalStore : IDecisionProposalStore, IDecisionRecoveryStore, IDecisionPersistenceHealth
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private volatile bool _available = true;
    private string? _lastFailure;

    public SqlDecisionProposalStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public bool IsAvailable => _available;
    public string? LastFailure => _lastFailure;

    public async Task<DecisionProposal> AddAsync(DecisionProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var json = JsonSerializer.Serialize(proposal, JsonOptions);
        var hash = Sha256(json);
        using var db = CreateDb();
        try
        {
            var existingEntity = await db.Queryable<DecisionProposalEntity>()
                .Where(x => x.ProposalId == proposal.ProposalId || x.IdempotencyKey == proposal.IdempotencyKey)
                .FirstAsync(ct);
            if (existingEntity is not null)
            {
                if (!string.Equals(existingEntity.ProposalHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Decision proposal idempotency conflict.");
                return Deserialize(existingEntity);
            }

            await db.Insertable(new DecisionProposalEntity
            {
                ProposalId = proposal.ProposalId,
                ProposalType = proposal.Type.ToString(),
                Status = proposal.Status.ToString(),
                CreatedAtUtc = proposal.CreatedAtUtc.UtcDateTime,
                ExpiresAtUtc = proposal.ExpiresAtUtc.UtcDateTime,
                CorrelationId = proposal.CorrelationId,
                IdempotencyKey = proposal.IdempotencyKey,
                ProposalJson = json,
                ProposalHash = hash
            }).ExecuteCommandAsync(ct);

            if (proposal.Constraints.Count > 0)
            {
                var constraints = proposal.Constraints.Select((x, ordinal) => new DecisionConstraintResultEntity
                {
                    ProposalId = proposal.ProposalId,
                    Code = x.Code,
                    Passed = x.Passed,
                    Reason = x.Reason,
                    EvidenceHash = x.EvidenceHash,
                    Ordinal = ordinal
                }).ToArray();
                await db.Insertable(constraints).ExecuteCommandAsync(ct);
            }

            await db.Insertable(new DecisionExplanationEvidenceEntity
            {
                ProposalId = proposal.ProposalId,
                ModelId = proposal.Evidence.ModelId,
                ModelVersion = proposal.Evidence.ModelVersion,
                FeatureSnapshotId = proposal.Evidence.FeatureSnapshotId,
                FeatureSchemaHash = proposal.Evidence.FeatureSchemaHash,
                ModelEvidenceHash = proposal.Evidence.EvidenceHash,
                ExplanationEvidenceHash = proposal.Explanation.EvidenceHash,
                ExplanationJson = JsonSerializer.Serialize(proposal.Explanation, JsonOptions)
            }).ExecuteCommandAsync(ct);

            Healthy();
            return proposal;
        }
        catch (Exception ex)
        {
            Failed(ex);
            throw;
        }
    }

    public async Task<DecisionProposal?> GetAsync(string proposalId, CancellationToken ct)
    {
        using var db = CreateDb();
        try
        {
            var entity = await db.Queryable<DecisionProposalEntity>()
                .Where(x => x.ProposalId == proposalId)
                .FirstAsync(ct);
            Healthy();
            return entity is null ? null : Deserialize(entity);
        }
        catch (Exception ex) { Failed(ex); throw; }
    }

    public async Task<IReadOnlyList<DecisionProposal>> QueryAsync(DecisionQuery query, CancellationToken ct)
    {
        query.Validate();
        using var db = CreateDb();
        try
        {
            var q = db.Queryable<DecisionProposalEntity>();
            if (query.FromUtc is not null) q = q.Where(x => x.CreatedAtUtc >= query.FromUtc.Value.UtcDateTime);
            if (query.ToUtc is not null) q = q.Where(x => x.CreatedAtUtc <= query.ToUtc.Value.UtcDateTime);
            if (query.Type is not null)
            {
                var type = query.Type.Value.ToString();
                q = q.Where(x => x.ProposalType == type);
            }
            if (query.Status is not null)
            {
                var status = query.Status.Value.ToString();
                q = q.Where(x => x.Status == status);
            }
            var entities = await q.OrderBy(x => x.CreatedAtUtc, OrderByType.Desc).Take(query.Take).ToListAsync(ct);
            Healthy();
            return entities.Select(Deserialize).ToArray();
        }
        catch (Exception ex) { Failed(ex); throw; }
    }

    public async Task<DecisionApprovalEntry> AppendApprovalAsync(DecisionApprovalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var db = CreateDb();
        try
        {
            var replay = await db.Queryable<DecisionApprovalJournalEntity>()
                .Where(x => x.IdempotencyKey == entry.IdempotencyKey)
                .FirstAsync(ct);
            if (replay is not null) return ToDomain(replay);

            var proposalEntity = await db.Queryable<DecisionProposalEntity>()
                .Where(x => x.ProposalId == entry.ProposalId)
                .FirstAsync(ct) ?? throw new KeyNotFoundException("Proposal not found.");
            var proposal = Deserialize(proposalEntity);
            if (proposal.Status != entry.FromStatus)
                throw new InvalidOperationException($"Proposal state changed from {entry.FromStatus} to {proposal.Status}.");

            await db.Insertable(new DecisionApprovalJournalEntity
            {
                ProposalId = entry.ProposalId,
                FromStatus = entry.FromStatus.ToString(),
                ToStatus = entry.ToStatus.ToString(),
                Actor = entry.Actor,
                Reason = entry.Reason,
                Utc = entry.Utc.UtcDateTime,
                CorrelationId = entry.CorrelationId,
                IdempotencyKey = entry.IdempotencyKey,
                EntryHash = entry.EntryHash
            }).ExecuteCommandAsync(ct);

            var updated = proposal with { Status = entry.ToStatus };
            proposalEntity.Status = entry.ToStatus.ToString();
            proposalEntity.ProposalJson = JsonSerializer.Serialize(updated, JsonOptions);
            proposalEntity.ProposalHash = Sha256(proposalEntity.ProposalJson);
            await db.Updateable(proposalEntity).ExecuteCommandAsync(ct);
            Healthy();
            return entry;
        }
        catch (Exception ex) { Failed(ex); throw; }
    }

    public async Task<DecisionOutcome> RecordOutcomeAsync(DecisionOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        using var db = CreateDb();
        try
        {
            var existing = await db.Queryable<DecisionOutcomeJournalEntity>()
                .Where(x => x.ProposalId == outcome.ProposalId)
                .FirstAsync(ct);
            if (existing is not null) return ToDomain(existing);

            var proposalEntity = await db.Queryable<DecisionProposalEntity>()
                .Where(x => x.ProposalId == outcome.ProposalId)
                .FirstAsync(ct) ?? throw new KeyNotFoundException("Proposal not found.");
            var proposal = Deserialize(proposalEntity);
            DecisionGovernancePolicy.ValidateOutcome(outcome, proposal.CreatedAtUtc);

            await db.Insertable(new DecisionOutcomeJournalEntity
            {
                ProposalId = outcome.ProposalId,
                OutcomeType = outcome.OutcomeType,
                ActualReference = outcome.ActualReference,
                ActualBenefit = outcome.ActualBenefit,
                ObservedAtUtc = outcome.ObservedAtUtc.UtcDateTime,
                EvidenceHash = outcome.EvidenceHash
            }).ExecuteCommandAsync(ct);

            var updated = proposal with { Status = DecisionProposalStatus.OutcomeRecorded };
            proposalEntity.Status = updated.Status.ToString();
            proposalEntity.ProposalJson = JsonSerializer.Serialize(updated, JsonOptions);
            proposalEntity.ProposalHash = Sha256(proposalEntity.ProposalJson);
            await db.Updateable(proposalEntity).ExecuteCommandAsync(ct);
            Healthy();
            return outcome;
        }
        catch (Exception ex) { Failed(ex); throw; }
    }

    public async Task<IReadOnlyList<DecisionApprovalEntry>> GetApprovalsAsync(string proposalId, int take, CancellationToken ct)
    {
        using var db = CreateDb();
        var entities = await db.Queryable<DecisionApprovalJournalEntity>()
            .Where(x => x.ProposalId == proposalId)
            .OrderBy(x => x.Utc, OrderByType.Desc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToArray();
    }

    public async Task<DecisionOutcome?> GetOutcomeAsync(string proposalId, CancellationToken ct)
    {
        using var db = CreateDb();
        var entity = await db.Queryable<DecisionOutcomeJournalEntity>()
            .Where(x => x.ProposalId == proposalId)
            .FirstAsync(ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<DecisionPersistenceRecovery> RecoverAsync(DateTimeOffset asOfUtc, int maximumProposals, CancellationToken ct)
    {
        var proposals = await QueryAsync(new DecisionQuery(ToUtc: asOfUtc, Take: Math.Clamp(maximumProposals, 1, 1000)), ct);
        var ids = proposals.Select(x => x.ProposalId).ToArray();
        if (ids.Length == 0) return new DecisionPersistenceRecovery([], [], []);
        using var db = CreateDb();
        var approvals = await db.Queryable<DecisionApprovalJournalEntity>().Where(x => ids.Contains(x.ProposalId)).ToListAsync(ct);
        var outcomes = await db.Queryable<DecisionOutcomeJournalEntity>().Where(x => ids.Contains(x.ProposalId)).ToListAsync(ct);
        return new DecisionPersistenceRecovery(proposals, approvals.Select(ToDomain).ToArray(), outcomes.Select(ToDomain).ToArray());
    }

    private SqlSugarClient CreateDb() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static DecisionProposal Deserialize(DecisionProposalEntity entity) =>
        JsonSerializer.Deserialize<DecisionProposal>(entity.ProposalJson, JsonOptions)
        ?? throw new InvalidOperationException($"Decision proposal '{entity.ProposalId}' JSON is invalid.");

    private static DecisionApprovalEntry ToDomain(DecisionApprovalJournalEntity entity) => new(
        entity.ProposalId,
        Enum.Parse<DecisionProposalStatus>(entity.FromStatus, true),
        Enum.Parse<DecisionProposalStatus>(entity.ToStatus, true),
        entity.Actor,
        entity.Reason,
        new DateTimeOffset(DateTime.SpecifyKind(entity.Utc, DateTimeKind.Utc)),
        entity.CorrelationId,
        entity.IdempotencyKey,
        entity.EntryHash);

    private static DecisionOutcome ToDomain(DecisionOutcomeJournalEntity entity) => new(
        entity.ProposalId,
        entity.OutcomeType,
        entity.ActualReference,
        entity.ActualBenefit,
        new DateTimeOffset(DateTime.SpecifyKind(entity.ObservedAtUtc, DateTimeKind.Utc)),
        entity.EvidenceHash);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void Healthy() { _available = true; _lastFailure = null; }
    private void Failed(Exception ex) { _available = false; _lastFailure = ex.Message; }
}
