namespace Wcs.Core.TransportScheduling;

using System.Text.Json;

public interface ITransportConfigurationSnapshotService
{
    Task<TransportConfigurationSnapshot> CreateAsync(
        string name,
        string reason,
        string createdBy,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransportConfigurationSnapshot>> GetAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default);

    Task<TransportConfigurationSnapshot?> GetAsync(
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task<TransportConfigurationRollbackResult> RollbackAsync(
        string snapshotId,
        long expectedRuntimeVersion,
        long expectedTuningVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
}

public sealed class TransportConfigurationSnapshotService : ITransportConfigurationSnapshotService
{
    private readonly ITransportConfigurationService _configuration;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportJournalStore _journal;
    private readonly ITransportTelemetryService _telemetry;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportConfigurationSnapshotService(
        ITransportConfigurationService configuration,
        ITransportProductionTuningService tuning,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportJournalStore journal,
        ITransportTelemetryService telemetry)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _singleTrack = singleTrack ?? throw new ArgumentNullException(nameof(singleTrack));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async Task<TransportConfigurationSnapshot> CreateAsync(
        string name,
        string reason,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("快照名称不能为空", nameof(name));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("快照原因不能为空", nameof(reason));
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new ArgumentException("创建人不能为空", nameof(createdBy));

        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.ConfigurationSnapshot,
            "transport.configuration.snapshot");
        try
        {
            var snapshot = new TransportConfigurationSnapshot
            {
                Name = name.Trim(),
                Reason = reason.Trim(),
                CreatedBy = createdBy,
                RuntimeConfiguration = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false),
                ProductionTuning = _tuning.Current,
                ProductionStations = _stations.GetAll()
                    .Select(x => new TransportStationDefinition
                    {
                        StationId = x.StationId,
                        Name = x.Name,
                        Capacity = x.Capacity,
                        MaximumQueuedTasks = x.MaximumQueuedTasks,
                        Enabled = x.Enabled
                    })
                    .ToArray(),
                SingleTrackSections = _singleTrack.GetSnapshots()
                    .Select(x => x.Definition)
                    .ToArray(),
                CreatedAtUtc = DateTime.UtcNow
            };
            await _journal.UpsertAsync(new TransportJournalRecord
            {
                Category = TransportJournalCategory.ConfigurationSnapshot,
                RecordId = snapshot.SnapshotId,
                PayloadJson = JsonSerializer.Serialize(snapshot),
                OccurredAtUtc = snapshot.CreatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
            operation.Complete(
                true,
                $"配置快照 {snapshot.SnapshotId} 已创建",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["snapshot.id"] = snapshot.SnapshotId,
                    ["runtime.version"] = snapshot.RuntimeConfiguration.Version.ToString(),
                    ["tuning.version"] = snapshot.ProductionTuning.Version.ToString()
                });
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<TransportConfigurationSnapshot>> GetAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ConfigurationSnapshot,
            Math.Clamp(maxCount, 1, 500),
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(x => JsonSerializer.Deserialize<TransportConfigurationSnapshot>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportConfigurationSnapshot>()
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToArray();
    }

    public async Task<TransportConfigurationSnapshot?> GetAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            return null;
        return (await GetAsync(500, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.SnapshotId, snapshotId, StringComparison.Ordinal));
    }

    public async Task<TransportConfigurationRollbackResult> RollbackAsync(
        string snapshotId,
        long expectedRuntimeVersion,
        long expectedTuningVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(updatedBy))
            return new TransportConfigurationRollbackResult { Error = "执行人不能为空" };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.ConfigurationRollback,
            "transport.configuration.rollback",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["snapshot.id"] = snapshotId
            });
        TransportConfigurationSnapshot? safety = null;
        try
        {
            var target = await GetAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                operation.Complete(false, "配置快照不存在");
                return new TransportConfigurationRollbackResult { Error = "配置快照不存在" };
            }

            var currentRuntime = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false);
            var currentTuning = _tuning.Current;
            if (currentRuntime.Version != expectedRuntimeVersion)
            {
                operation.Complete(false, "运行时配置版本已变化");
                return new TransportConfigurationRollbackResult { Error = "运行时配置版本已变化，请重新确认" };
            }
            if (currentTuning.Version != expectedTuningVersion)
            {
                operation.Complete(false, "生产整定参数版本已变化");
                return new TransportConfigurationRollbackResult { Error = "生产整定参数版本已变化，请重新确认" };
            }

            safety = await CreateAsync(
                $"rollback-safety-{DateTime.UtcNow:yyyyMMddHHmmss}",
                $"回滚到 {snapshotId} 前的自动安全快照",
                updatedBy,
                cancellationToken).ConfigureAwait(false);

            var runtimeResult = await _configuration.SaveAndApplyAsync(
                target.RuntimeConfiguration,
                currentRuntime.Version,
                updatedBy,
                cancellationToken).ConfigureAwait(false);
            if (!runtimeResult.Success || runtimeResult.Configuration is null)
            {
                operation.Complete(false, runtimeResult.Error);
                return new TransportConfigurationRollbackResult
                {
                    Error = runtimeResult.Error,
                    SafetySnapshotId = safety.SnapshotId
                };
            }

            var tuningResult = await _tuning.SaveAsync(
                target.ProductionTuning,
                currentTuning.Version,
                updatedBy,
                cancellationToken).ConfigureAwait(false);
            if (!tuningResult.Success || tuningResult.Options is null)
            {
                await _configuration.SaveAndApplyAsync(
                    safety.RuntimeConfiguration,
                    runtimeResult.Configuration.Version,
                    updatedBy,
                    cancellationToken).ConfigureAwait(false);
                operation.Complete(false, tuningResult.Error);
                return new TransportConfigurationRollbackResult
                {
                    Error = tuningResult.Error,
                    SafetySnapshotId = safety.SnapshotId
                };
            }

            try
            {
                await ApplyDefinitionsAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _configuration.SaveAndApplyAsync(
                    safety.RuntimeConfiguration,
                    runtimeResult.Configuration.Version,
                    updatedBy,
                    cancellationToken).ConfigureAwait(false);
                await _tuning.SaveAsync(
                    safety.ProductionTuning,
                    tuningResult.Options.Version,
                    updatedBy,
                    cancellationToken).ConfigureAwait(false);
                await ApplyDefinitionsAsync(safety, cancellationToken).ConfigureAwait(false);
                throw;
            }

            operation.Complete(
                true,
                $"已回滚到配置快照 {snapshotId}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["safety.snapshot.id"] = safety.SnapshotId
                });
            return new TransportConfigurationRollbackResult
            {
                Success = true,
                SafetySnapshotId = safety.SnapshotId,
                AppliedSnapshot = target
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            return new TransportConfigurationRollbackResult
            {
                Error = ex.Message,
                SafetySnapshotId = safety?.SnapshotId
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyDefinitionsAsync(
        TransportConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var targetStationIds = snapshot.ProductionStations
            .Select(x => x.StationId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var current in _stations.GetAll().Where(x => !targetStationIds.Contains(x.StationId)))
        {
            await _stations.SaveDefinitionAsync(new TransportStationDefinition
            {
                StationId = current.StationId,
                Name = current.Name,
                Capacity = Math.Max(1, current.Capacity),
                MaximumQueuedTasks = current.MaximumQueuedTasks,
                Enabled = false
            }, cancellationToken).ConfigureAwait(false);
        }
        foreach (var station in snapshot.ProductionStations)
            await _stations.SaveDefinitionAsync(station, cancellationToken).ConfigureAwait(false);

        var targetSectionIds = snapshot.SingleTrackSections
            .Select(x => x.SectionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var current in _singleTrack.GetSnapshots().Where(x => !targetSectionIds.Contains(x.Definition.SectionId)))
        {
            await _singleTrack.SaveDefinitionAsync(
                current.Definition with { Enabled = false },
                cancellationToken).ConfigureAwait(false);
        }
        foreach (var section in snapshot.SingleTrackSections)
            await _singleTrack.SaveDefinitionAsync(section, cancellationToken).ConfigureAwait(false);
    }
}
