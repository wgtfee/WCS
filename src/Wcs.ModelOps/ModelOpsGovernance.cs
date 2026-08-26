namespace Wcs.ModelOps;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Wcs.IndustrialIntelligence.Governance;

public sealed record ModelQuarantineRequest(
    string ModelId,
    string Version,
    string AssetType,
    string Profile,
    string Actor,
    string Reason,
    string CorrelationId);

public sealed record ModelDeploymentRecoveryReport(
    bool IsHealthy,
    IReadOnlyList<string> Errors,
    int ScopeCount,
    int ChampionCount,
    int FallbackCount,
    int ShadowCount,
    int QuarantinedCount,
    DateTimeOffset CheckedAtUtc);

public interface IModelDeploymentStore
{
    Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct);

    Task<IReadOnlyList<AiModelDeployment>> ListAllAsync(CancellationToken ct);

    Task ApplyAsync(
        IReadOnlyList<AiModelDeployment> deployments,
        CancellationToken ct);
}

public interface IModelOpsAuditJournal
{
    Task AppendAsync(AiModelAuditEntry entry, CancellationToken ct);

    Task<IReadOnlyList<AiModelAuditEntry>> ListAsync(
        string? modelId,
        int limit,
        CancellationToken ct);
}

public interface IModelEvaluationStore
{
    Task AppendAsync(AiModelEvaluation evaluation, CancellationToken ct);

    Task<IReadOnlyList<AiModelEvaluation>> ListAsync(
        string modelId,
        int limit,
        CancellationToken ct);
}

public interface IModelDriftStore
{
    Task AppendAsync(AiModelDriftEvent driftEvent, CancellationToken ct);

    Task<IReadOnlyList<AiModelDriftEvent>> ListAsync(
        string modelId,
        int limit,
        CancellationToken ct);
}

public interface IModelDeploymentGovernanceManager : IModelDeploymentManager
{
    Task QuarantineAsync(ModelQuarantineRequest request, CancellationToken ct);

    Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct);
}

public sealed class ModelDeploymentInvariantException : InvalidOperationException
{
    public ModelDeploymentInvariantException(string message)
        : base(message)
    {
    }
}

public sealed class InMemoryModelDeploymentStore : IModelDeploymentStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AiModelDeployment> _deployments =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AiModelDeployment>>(
                _deployments.Values
                    .Where(x => ScopeMatches(x, modelId, assetType, profile))
                    .OrderBy(x => x.ModelVersion, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public Task<IReadOnlyList<AiModelDeployment>> ListAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AiModelDeployment>>(
                _deployments.Values
                    .OrderBy(x => x.ModelId, StringComparer.Ordinal)
                    .ThenBy(x => x.AssetType, StringComparer.Ordinal)
                    .ThenBy(x => x.Profile, StringComparer.Ordinal)
                    .ThenBy(x => x.ModelVersion, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public Task ApplyAsync(IReadOnlyList<AiModelDeployment> deployments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var next = new Dictionary<string, AiModelDeployment>(_deployments, StringComparer.OrdinalIgnoreCase);
            foreach (var deployment in deployments)
                next[Key(deployment)] = deployment;

            ModelDeploymentInvariants.ThrowIfInvalid(next.Values);
            _deployments.Clear();
            foreach (var pair in next)
                _deployments[pair.Key] = pair.Value;
        }

        return Task.CompletedTask;
    }

    private static string Key(AiModelDeployment x) =>
        $"{x.ModelId.Trim()}\u001f{x.ModelVersion.Trim()}\u001f{x.AssetType.Trim()}\u001f{x.Profile.Trim()}";

    private static bool ScopeMatches(AiModelDeployment x, string modelId, string assetType, string profile) =>
        string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.AssetType, assetType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Profile, profile, StringComparison.OrdinalIgnoreCase);
}

public sealed class InMemoryModelOpsAuditJournal : IModelOpsAuditJournal
{
    /// <summary>内存审计条目上限：超限后淘汰最旧条目（7×24 运行下防止无界增长）。</summary>
    private const int MaxEntries = 10_000;

    private readonly object _sync = new();
    private readonly List<AiModelAuditEntry> _entries = [];
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendAsync(AiModelAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        ValidateAudit(entry);

        lock (_sync)
        {
            if (!_ids.Add(entry.AuditId))
                throw new InvalidOperationException($"AuditId '{entry.AuditId}' already exists. Audit journal is append-only.");
            _entries.Add(entry);
            TrimUnsafe();
        }
        return Task.CompletedTask;
    }

    private void TrimUnsafe()
    {
        if (_entries.Count <= MaxEntries) return;
        var overflow = _entries.Count - MaxEntries;
        for (var i = 0; i < overflow; i++)
            _ids.Remove(_entries[i].AuditId);
        _entries.RemoveRange(0, overflow);
    }

    public Task<IReadOnlyList<AiModelAuditEntry>> ListAsync(
        string? modelId,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 1000);
        lock (_sync)
        {
            var query = _entries.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(modelId))
                query = query.Where(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<IReadOnlyList<AiModelAuditEntry>>(
                query.OrderByDescending(x => x.OccurredAtUtc).Take(limit).ToArray());
        }
    }

    private static void ValidateAudit(AiModelAuditEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.AuditId) ||
            string.IsNullOrWhiteSpace(entry.Action) ||
            string.IsNullOrWhiteSpace(entry.ModelId) ||
            string.IsNullOrWhiteSpace(entry.ModelVersion) ||
            string.IsNullOrWhiteSpace(entry.CorrelationId))
            throw new ArgumentException("AuditId, Action, ModelId, ModelVersion and CorrelationId are required.", nameof(entry));
        _ = ActorReason.Create(entry.Actor, entry.Reason);
        if (!Hashing.IsSha256(entry.PayloadHash))
            throw new ArgumentException("PayloadHash must be SHA-256.", nameof(entry));
    }
}

public sealed class InMemoryModelEvaluationStore : IModelEvaluationStore
{
    /// <summary>内存评估记录上限。</summary>
    private const int MaxEntries = 10_000;

    private readonly object _sync = new();
    private readonly List<AiModelEvaluation> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendAsync(AiModelEvaluation evaluation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ct.ThrowIfCancellationRequested();
        if (!Hashing.IsSha256(evaluation.DatasetHash) || !Hashing.IsSha256(evaluation.EvidenceSha256))
            throw new ArgumentException("Evaluation hashes must be SHA-256.", nameof(evaluation));
        lock (_sync)
        {
            if (!_ids.Add(evaluation.EvaluationId))
                throw new InvalidOperationException($"EvaluationId '{evaluation.EvaluationId}' already exists.");
            _items.Add(evaluation);
            TrimUnsafe();
        }
        return Task.CompletedTask;
    }

    private void TrimUnsafe()
    {
        if (_items.Count <= MaxEntries) return;
        var overflow = _items.Count - MaxEntries;
        for (var i = 0; i < overflow; i++)
            _ids.Remove(_items[i].EvaluationId);
        _items.RemoveRange(0, overflow);
    }

    public Task<IReadOnlyList<AiModelEvaluation>> ListAsync(string modelId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 1000);
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AiModelEvaluation>>(
                _items.Where(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }
}

public sealed class InMemoryModelDriftStore : IModelDriftStore
{
    /// <summary>内存漂移记录上限。</summary>
    private const int MaxEntries = 10_000;

    private readonly object _sync = new();
    private readonly List<AiModelDriftEvent> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendAsync(AiModelDriftEvent driftEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(driftEvent);
        ct.ThrowIfCancellationRequested();
        if (!Hashing.IsSha256(driftEvent.EvidenceSha256))
            throw new ArgumentException("EvidenceSha256 must be SHA-256.", nameof(driftEvent));
        lock (_sync)
        {
            if (!_ids.Add(driftEvent.DriftEventId))
                throw new InvalidOperationException($"DriftEventId '{driftEvent.DriftEventId}' already exists.");
            _items.Add(driftEvent);
            TrimUnsafe();
        }
        return Task.CompletedTask;
    }

    private void TrimUnsafe()
    {
        if (_items.Count <= MaxEntries) return;
        var overflow = _items.Count - MaxEntries;
        for (var i = 0; i < overflow; i++)
            _ids.Remove(_items[i].DriftEventId);
        _items.RemoveRange(0, overflow);
    }

    public Task<IReadOnlyList<AiModelDriftEvent>> ListAsync(string modelId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 1000);
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AiModelDriftEvent>>(
                _items.Where(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.OccurredAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }
}

public static class ModelDeploymentInvariants
{
    public static IReadOnlyList<string> Validate(IEnumerable<AiModelDeployment> deployments)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        var errors = new List<string>();

        foreach (var group in deployments.GroupBy(
                     x => $"{x.ModelId}\u001f{x.AssetType}\u001f{x.Profile}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var champions = group.Count(x => x.Status == AiModelLifecycleStatus.Champion);
            var fallbacks = group.Count(x => x.Status == AiModelLifecycleStatus.Fallback);
            if (champions > 1)
                errors.Add($"Scope '{group.Key}' has {champions} Champion deployments; maximum is one.");
            if (fallbacks > 1)
                errors.Add($"Scope '{group.Key}' has {fallbacks} Fallback deployments; maximum is one.");
        }

        return errors;
    }

    public static void ThrowIfInvalid(IEnumerable<AiModelDeployment> deployments)
    {
        var errors = Validate(deployments);
        if (errors.Count > 0)
            throw new ModelDeploymentInvariantException(string.Join(" ", errors));
    }
}

public sealed class ModelDeploymentRecoveryService
{
    private readonly IModelRegistry _registry;
    private readonly IModelDeploymentStore _store;

    public ModelDeploymentRecoveryService(IModelRegistry registry, IModelDeploymentStore store)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ModelDeploymentRecoveryReport> ValidateAsync(CancellationToken ct)
    {
        var deployments = await _store.ListAllAsync(ct);
        var errors = ModelDeploymentInvariants.Validate(deployments).ToList();

        foreach (var deployment in deployments.Where(x =>
                     x.Status is AiModelLifecycleStatus.Champion or AiModelLifecycleStatus.Fallback or AiModelLifecycleStatus.Shadow))
        {
            var version = await _registry.GetAsync(deployment.ModelId, deployment.ModelVersion, ct);
            if (version is null)
            {
                errors.Add($"Deployment references missing registry version '{deployment.ModelId}/{deployment.ModelVersion}'.");
                continue;
            }

            if (!ModelOpsContractRules.IsApproved(version.Manifest))
                errors.Add($"Active deployment '{deployment.ModelId}/{deployment.ModelVersion}' is not approved.");
        }

        return new ModelDeploymentRecoveryReport(
            errors.Count == 0,
            errors,
            deployments
                .Select(x => $"{x.ModelId}\u001f{x.AssetType}\u001f{x.Profile}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            deployments.Count(x => x.Status == AiModelLifecycleStatus.Champion),
            deployments.Count(x => x.Status == AiModelLifecycleStatus.Fallback),
            deployments.Count(x => x.Status == AiModelLifecycleStatus.Shadow),
            deployments.Count(x => x.Status == AiModelLifecycleStatus.Quarantined),
            DateTimeOffset.UtcNow);
    }

    public async Task EnsureHealthyAsync(CancellationToken ct)
    {
        var report = await ValidateAsync(ct);
        if (!report.IsHealthy)
            throw new ModelDeploymentInvariantException(
                "ModelOps restart recovery failed closed: " + string.Join(" ", report.Errors));
    }
}

public sealed class PersistentModelDeploymentManager : IModelDeploymentGovernanceManager
{
    private readonly IModelRegistry _registry;
    private readonly IModelDeploymentStore _store;
    private readonly IModelOpsAuditJournal _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PersistentModelDeploymentManager(
        IModelRegistry registry,
        IModelDeploymentStore store,
        IModelOpsAuditJournal audit)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task PromoteToShadowAsync(ModelDeploymentRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        var version = await RequireVersionAsync(request.ModelId, request.Version, ct);
        if (!ModelOpsContractRules.IsApproved(version.Manifest))
            throw new InvalidOperationException("Only an approved model version may enter Shadow.");

        await _gate.WaitAsync(ct);
        try
        {
            var scope = await _store.ListScopeAsync(request.ModelId, request.AssetType, request.Profile, ct);
            ModelDeploymentInvariants.ThrowIfInvalid(scope);
            var existing = scope.SingleOrDefault(x =>
                string.Equals(x.ModelVersion, request.Version, StringComparison.OrdinalIgnoreCase));
            if (existing?.Status is AiModelLifecycleStatus.Champion or AiModelLifecycleStatus.Fallback)
                throw new InvalidOperationException("Champion or Fallback must not be demoted to Shadow implicitly.");
            if (existing?.Status == AiModelLifecycleStatus.Quarantined)
                throw new InvalidOperationException("Quarantined model version requires a new immutable version before reuse.");

            var deployment = NewDeployment(request, AiModelLifecycleStatus.Shadow);
            await _store.ApplyAsync([deployment], ct);
            await AppendAuditAsync("PromoteToShadow", deployment, request.Actor, request.Reason, request.CorrelationId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PromoteToChampionAsync(ModelDeploymentRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        var version = await RequireVersionAsync(request.ModelId, request.Version, ct);
        if (!ModelOpsContractRules.IsApproved(version.Manifest))
            throw new InvalidOperationException("Only an approved model version may become Champion.");

        await _gate.WaitAsync(ct);
        try
        {
            var scope = await _store.ListScopeAsync(request.ModelId, request.AssetType, request.Profile, ct);
            ModelDeploymentInvariants.ThrowIfInvalid(scope);
            var candidate = scope.SingleOrDefault(x =>
                string.Equals(x.ModelVersion, request.Version, StringComparison.OrdinalIgnoreCase));
            if (candidate is null || candidate.Status != AiModelLifecycleStatus.Shadow)
                throw new InvalidOperationException("A model must be in Shadow before Champion promotion.");

            var now = DateTimeOffset.UtcNow;
            var updates = new List<AiModelDeployment>();
            var oldFallback = scope.SingleOrDefault(x => x.Status == AiModelLifecycleStatus.Fallback);
            var oldChampion = scope.SingleOrDefault(x => x.Status == AiModelLifecycleStatus.Champion);

            if (oldFallback is not null)
            {
                updates.Add(oldFallback with
                {
                    Status = AiModelLifecycleStatus.Retired,
                    UpdatedAtUtc = now,
                    Actor = request.Actor.Trim(),
                    Reason = $"retired to preserve single fallback: {request.Reason.Trim()}",
                    CorrelationId = request.CorrelationId.Trim()
                });
            }

            if (oldChampion is not null)
            {
                updates.Add(oldChampion with
                {
                    Status = AiModelLifecycleStatus.Fallback,
                    UpdatedAtUtc = now,
                    Actor = request.Actor.Trim(),
                    Reason = $"fallback after champion promotion: {request.Reason.Trim()}",
                    CorrelationId = request.CorrelationId.Trim()
                });
            }

            var champion = candidate with
            {
                Status = AiModelLifecycleStatus.Champion,
                UpdatedAtUtc = now,
                Actor = request.Actor.Trim(),
                Reason = request.Reason.Trim(),
                CorrelationId = request.CorrelationId.Trim()
            };
            updates.Add(champion);

            await _store.ApplyAsync(updates, ct);
            await AppendAuditAsync("PromoteToChampion", champion, request.Actor, request.Reason, request.CorrelationId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct)
    {
        ValidateRollbackRequest(request);
        await _gate.WaitAsync(ct);
        try
        {
            var scope = await _store.ListScopeAsync(request.ModelId, request.AssetType, request.Profile, ct);
            ModelDeploymentInvariants.ThrowIfInvalid(scope);
            var fallback = scope.SingleOrDefault(x => x.Status == AiModelLifecycleStatus.Fallback)
                ?? throw new InvalidOperationException("No valid Fallback deployment exists for this scope.");
            var champion = scope.SingleOrDefault(x => x.Status == AiModelLifecycleStatus.Champion);

            var fallbackVersion = await RequireVersionAsync(fallback.ModelId, fallback.ModelVersion, ct);
            if (!ModelOpsContractRules.IsApproved(fallbackVersion.Manifest))
                throw new InvalidOperationException("Fallback version is not approved; rollback fails closed.");

            var now = DateTimeOffset.UtcNow;
            var nextChampion = fallback with
            {
                Status = AiModelLifecycleStatus.Champion,
                UpdatedAtUtc = now,
                Actor = request.Actor.Trim(),
                Reason = request.Reason.Trim(),
                CorrelationId = request.CorrelationId.Trim()
            };
            var updates = new List<AiModelDeployment> { nextChampion };
            if (champion is not null)
            {
                updates.Add(champion with
                {
                    Status = AiModelLifecycleStatus.Fallback,
                    UpdatedAtUtc = now,
                    Actor = request.Actor.Trim(),
                    Reason = $"fallback after rollback: {request.Reason.Trim()}",
                    CorrelationId = request.CorrelationId.Trim()
                });
            }

            await _store.ApplyAsync(updates, ct);
            await AppendAuditAsync("RollbackToFallback", nextChampion, request.Actor, request.Reason, request.CorrelationId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task QuarantineAsync(ModelQuarantineRequest request, CancellationToken ct)
    {
        ValidateQuarantineRequest(request);
        _ = await RequireVersionAsync(request.ModelId, request.Version, ct);

        await _gate.WaitAsync(ct);
        try
        {
            var scope = await _store.ListScopeAsync(request.ModelId, request.AssetType, request.Profile, ct);
            ModelDeploymentInvariants.ThrowIfInvalid(scope);
            var target = scope.SingleOrDefault(x =>
                string.Equals(x.ModelVersion, request.Version, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Deployment does not exist in the requested scope.");

            var quarantined = target with
            {
                Status = AiModelLifecycleStatus.Quarantined,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Actor = request.Actor.Trim(),
                Reason = request.Reason.Trim(),
                CorrelationId = request.CorrelationId.Trim()
            };
            await _store.ApplyAsync([quarantined], ct);
            await AppendAuditAsync(
                target.Status == AiModelLifecycleStatus.Champion
                    ? "QuarantineChampionFailClosed"
                    : "Quarantine",
                quarantined,
                request.Actor,
                request.Reason,
                request.CorrelationId,
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct)
    {
        Require(modelId, nameof(modelId));
        Require(assetType, nameof(assetType));
        Require(profile, nameof(profile));
        var scope = await _store.ListScopeAsync(modelId, assetType, profile, ct);
        ModelDeploymentInvariants.ThrowIfInvalid(scope);
        return scope;
    }

    private async Task<AiModelVersion> RequireVersionAsync(string modelId, string version, CancellationToken ct) =>
        await _registry.GetAsync(modelId, version, ct)
        ?? throw new InvalidOperationException($"Model '{modelId}' version '{version}' is not registered.");

    private async Task AppendAuditAsync(
        string action,
        AiModelDeployment deployment,
        string actor,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            deployment.ModelId,
            deployment.ModelVersion,
            deployment.AssetType,
            deployment.Profile,
            Status = deployment.Status.ToString(),
            deployment.UpdatedAtUtc,
            controlWriteAllowed = false,
            maximumAutomationLevel = "L1"
        });
        var entry = new AiModelAuditEntry(
            Guid.NewGuid().ToString("N"),
            action,
            deployment.ModelId,
            deployment.ModelVersion,
            actor.Trim(),
            reason.Trim(),
            DateTimeOffset.UtcNow,
            correlationId.Trim(),
            Hashing.Sha256(payload));
        await _audit.AppendAsync(entry, ct);
    }

    private static AiModelDeployment NewDeployment(ModelDeploymentRequest request, AiModelLifecycleStatus status) =>
        new(
            request.ModelId.Trim(),
            request.Version.Trim(),
            request.AssetType.Trim(),
            request.Profile.Trim(),
            status,
            DateTimeOffset.UtcNow,
            request.Actor.Trim(),
            request.Reason.Trim(),
            request.CorrelationId.Trim());

    private static void ValidateRequest(ModelDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ModelId, nameof(request.ModelId));
        Require(request.Version, nameof(request.Version));
        Require(request.AssetType, nameof(request.AssetType));
        Require(request.Profile, nameof(request.Profile));
        Require(request.CorrelationId, nameof(request.CorrelationId));
        _ = ActorReason.Create(request.Actor, request.Reason);
    }

    private static void ValidateRollbackRequest(ModelRollbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ModelId, nameof(request.ModelId));
        Require(request.AssetType, nameof(request.AssetType));
        Require(request.Profile, nameof(request.Profile));
        Require(request.CorrelationId, nameof(request.CorrelationId));
        _ = ActorReason.Create(request.Actor, request.Reason);
    }

    private static void ValidateQuarantineRequest(ModelQuarantineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ModelId, nameof(request.ModelId));
        Require(request.Version, nameof(request.Version));
        Require(request.AssetType, nameof(request.AssetType));
        Require(request.Profile, nameof(request.Profile));
        Require(request.CorrelationId, nameof(request.CorrelationId));
        _ = ActorReason.Create(request.Actor, request.Reason);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }
}

public sealed record ModelInferenceInput(
    string AssetId,
    string FeatureSchemaId,
    IReadOnlyDictionary<string, double> Features,
    DateTimeOffset ObservedAtUtc,
    string CorrelationId);

public sealed record ModelInferenceResult(
    string ModelId,
    string ModelVersion,
    IReadOnlyList<double> Outputs,
    long DurationMilliseconds,
    string EvidenceSha256,
    DateTimeOffset CompletedAtUtc);

public interface IModelInferenceRunner
{
    Task<ModelInferenceResult> RunAsync(
        AiModelVersion version,
        ModelInferenceInput input,
        CancellationToken ct);
}

public sealed record ShadowInferenceRecord(
    string RecordId,
    string ModelId,
    string ModelVersion,
    string AssetType,
    string Profile,
    string AssetId,
    string FeatureSchemaId,
    string EvidenceSha256,
    long DurationMilliseconds,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    bool ControlWriteAllowed);

public interface IShadowInferenceJournal
{
    Task AppendAsync(ShadowInferenceRecord record, CancellationToken ct);
    Task<IReadOnlyList<ShadowInferenceRecord>> ListAsync(string modelId, int limit, CancellationToken ct);
}

public sealed class InMemoryShadowInferenceJournal : IShadowInferenceJournal
{
    /// <summary>内存影子推理记录上限（高频推理下防止无界增长）。</summary>
    private const int MaxEntries = 20_000;

    private readonly object _sync = new();
    private readonly List<ShadowInferenceRecord> _items = [];

    public Task AppendAsync(ShadowInferenceRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        if (record.ControlWriteAllowed)
            throw new InvalidOperationException("IDI-P1 Shadow evidence must never allow control writes.");
        if (!Hashing.IsSha256(record.EvidenceSha256))
            throw new ArgumentException("EvidenceSha256 must be SHA-256.", nameof(record));
        lock (_sync)
        {
            _items.Add(record);
            if (_items.Count > MaxEntries)
                _items.RemoveRange(0, _items.Count - MaxEntries);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShadowInferenceRecord>> ListAsync(string modelId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 1000);
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<ShadowInferenceRecord>>(
                _items.Where(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.OccurredAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }
}

public sealed class GovernedShadowRuntime
{
    private readonly IModelRegistry _registry;
    private readonly IModelDeploymentStore _deployments;
    private readonly IModelInferenceRunner _runner;
    private readonly IShadowInferenceJournal _journal;

    public GovernedShadowRuntime(
        IModelRegistry registry,
        IModelDeploymentStore deployments,
        IModelInferenceRunner runner,
        IShadowInferenceJournal journal)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async Task<IReadOnlyList<ModelInferenceResult>> ExecuteAsync(
        string modelId,
        string assetType,
        string profile,
        ModelInferenceInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scope = await _deployments.ListScopeAsync(modelId, assetType, profile, ct);
        ModelDeploymentInvariants.ThrowIfInvalid(scope);
        var shadows = scope.Where(x => x.Status == AiModelLifecycleStatus.Shadow).ToArray();
        var results = new List<ModelInferenceResult>(shadows.Length);

        foreach (var deployment in shadows)
        {
            var version = await _registry.GetAsync(deployment.ModelId, deployment.ModelVersion, ct)
                ?? throw new InvalidOperationException("Shadow deployment references an unregistered model version.");
            if (!ModelOpsContractRules.IsApproved(version.Manifest))
                throw new InvalidOperationException("Unapproved model cannot execute in Shadow.");
            if (!string.Equals(version.Manifest.FeatureSchemaId, input.FeatureSchemaId, StringComparison.Ordinal))
                throw new InvalidOperationException("FeatureSchemaId mismatch; Shadow inference fails closed.");

            var stopwatch = Stopwatch.StartNew();
            var result = await _runner.RunAsync(version, input, ct);
            stopwatch.Stop();
            var duration = Math.Max(result.DurationMilliseconds, stopwatch.ElapsedMilliseconds);
            if (duration > version.Manifest.RuntimeLimits.MaximumInferenceMilliseconds)
                throw new TimeoutException("Shadow inference exceeded MaximumInferenceMilliseconds.");
            if (!Hashing.IsSha256(result.EvidenceSha256))
                throw new InvalidOperationException("Shadow runner returned invalid EvidenceSha256.");

            var normalized = result with { DurationMilliseconds = duration };
            results.Add(normalized);
            await _journal.AppendAsync(
                new ShadowInferenceRecord(
                    Guid.NewGuid().ToString("N"),
                    deployment.ModelId,
                    deployment.ModelVersion,
                    deployment.AssetType,
                    deployment.Profile,
                    input.AssetId,
                    input.FeatureSchemaId,
                    normalized.EvidenceSha256,
                    normalized.DurationMilliseconds,
                    normalized.CompletedAtUtc,
                    input.CorrelationId,
                    ControlWriteAllowed: false),
                ct);
        }

        return results;
    }
}

public sealed record ChampionChallengerObservation(
    string AssetId,
    double ChampionValue,
    double ChallengerValue,
    double? ActualValue);

public sealed record ChampionChallengerEvaluationResult(
    AiModelEvaluation Evaluation,
    bool ChallengerIsBetter,
    double ChampionMae,
    double ChallengerMae,
    double MeanAbsoluteDelta,
    int SampleCount,
    bool AutoPromotionAllowed);

public sealed class ChampionChallengerEvaluator
{
    private readonly IModelEvaluationStore _store;

    public ChampionChallengerEvaluator(IModelEvaluationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ChampionChallengerEvaluationResult> EvaluateAsync(
        string modelId,
        string challengerVersion,
        string datasetVersion,
        string datasetHash,
        IReadOnlyList<ChampionChallengerObservation> observations,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(challengerVersion) ||
            string.IsNullOrWhiteSpace(datasetVersion) || string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Model, version, dataset and correlation identifiers are required.");
        if (!Hashing.IsSha256(datasetHash))
            throw new ArgumentException("DatasetHash must be SHA-256.", nameof(datasetHash));
        if (observations is null || observations.Count == 0 || observations.Count > 100_000)
            throw new ArgumentOutOfRangeException(nameof(observations), "Observation count must be in [1,100000].");

        var withActual = observations.Where(x => x.ActualValue.HasValue).ToArray();
        var championMae = withActual.Length == 0
            ? double.NaN
            : withActual.Average(x => Math.Abs(x.ChampionValue - x.ActualValue!.Value));
        var challengerMae = withActual.Length == 0
            ? double.NaN
            : withActual.Average(x => Math.Abs(x.ChallengerValue - x.ActualValue!.Value));
        var delta = observations.Average(x => Math.Abs(x.ChampionValue - x.ChallengerValue));
        var challengerBetter = withActual.Length > 0 && challengerMae < championMae;

        var metrics = new Dictionary<string, object?>
        {
            ["sampleCount"] = observations.Count,
            ["labeledSampleCount"] = withActual.Length,
            ["championMae"] = double.IsNaN(championMae) ? null : championMae,
            ["challengerMae"] = double.IsNaN(challengerMae) ? null : challengerMae,
            ["meanAbsoluteDelta"] = delta,
            ["challengerIsBetter"] = challengerBetter,
            ["autoPromotionAllowed"] = false
        };
        var metricsJson = JsonSerializer.Serialize(metrics);
        var evidence = JsonSerializer.Serialize(new
        {
            modelId,
            challengerVersion,
            datasetVersion,
            datasetHash,
            observations,
            metrics,
            controlWriteAllowed = false,
            autoPromotionAllowed = false
        });
        var evaluation = new AiModelEvaluation(
            Guid.NewGuid().ToString("N"),
            modelId.Trim(),
            challengerVersion.Trim(),
            datasetVersion.Trim(),
            datasetHash.ToLowerInvariant(),
            metricsJson,
            Hashing.Sha256(evidence),
            DateTimeOffset.UtcNow,
            correlationId.Trim());
        await _store.AppendAsync(evaluation, ct);

        return new ChampionChallengerEvaluationResult(
            evaluation,
            challengerBetter,
            championMae,
            challengerMae,
            delta,
            observations.Count,
            AutoPromotionAllowed: false);
    }
}

public sealed class ModelDriftMonitor
{
    private readonly IModelDriftStore _store;

    public ModelDriftMonitor(IModelDriftStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AiModelDriftEvent?> ObserveAsync(
        string modelId,
        string modelVersion,
        string driftKind,
        double observedValue,
        double threshold,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(modelVersion) ||
            string.IsNullOrWhiteSpace(driftKind) || string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Model, version, drift kind and correlation identifiers are required.");
        if (!double.IsFinite(observedValue) || !double.IsFinite(threshold) || threshold < 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        if (observedValue <= threshold)
            return null;

        var evidence = JsonSerializer.Serialize(new
        {
            modelId,
            modelVersion,
            driftKind,
            observedValue,
            threshold,
            action = "evidence-only",
            autoQuarantineAllowed = false,
            controlWriteAllowed = false
        });
        var drift = new AiModelDriftEvent(
            Guid.NewGuid().ToString("N"),
            modelId.Trim(),
            modelVersion.Trim(),
            driftKind.Trim(),
            observedValue,
            threshold,
            DateTimeOffset.UtcNow,
            Hashing.Sha256(evidence),
            correlationId.Trim());
        await _store.AppendAsync(drift, ct);
        return drift;
    }
}
