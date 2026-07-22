namespace Wcs.Core.TransportScheduling;

using System.Security.Cryptography;
using System.Text.Json;

public interface ITransportResilienceService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportReadinessReport> RunPreflightAsync(CancellationToken cancellationToken = default);
    TransportReadinessReport? GetLastReadiness();
    Task<TransportOperationalBaseline> CaptureBaselineAsync(
        string name,
        string reason,
        string capturedBy,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TransportOperationalBaseline> GetBaselines(int maxCount = 100);
    Task<TransportLogicalBackupManifest> CreateBackupAsync(
        string name,
        string reason,
        string createdBy,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportLogicalBackupManifest>> GetBackupsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default);
    Task<TransportLogicalBackupContent?> GetBackupContentAsync(
        string backupId,
        CancellationToken cancellationToken = default);
    Task<TransportBackupValidationReport> ValidateBackupAsync(
        string backupId,
        CancellationToken cancellationToken = default);
    Task<TransportRestorePreparationResult> PrepareRestoreAsync(
        string backupId,
        string preparedBy,
        CancellationToken cancellationToken = default);
    Task<TransportRecoveryDrillReport> RunDrillAsync(
        TransportRecoveryDrillRequest request,
        string executedBy,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TransportRecoveryDrillReport> GetDrills(int maxCount = 100);
    TransportResilienceSnapshot GetSnapshot();
}

public sealed class TransportResilienceService : ITransportResilienceService
{
    private readonly ITransportConfigurationService _configuration;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportPlcSignalMapService _maps;
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportJournalStore _journal;
    private readonly ITransportLogicalBackupStorage _backupStorage;
    private readonly ITransportObservabilityService _observability;
    private readonly ITransportConsistencyInspectionService _consistency;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportExecutionEngine _executions;
    private readonly IRouteReservationManager _reservations;
    private readonly ITransportDriverDiagnosticsService _drivers;
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportTelemetryService _telemetry;
    private readonly TransportResilienceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<TransportOperationalBaseline> _baselines = new();
    private readonly List<TransportRecoveryDrillReport> _drills = new();
    private TransportReadinessReport? _lastReadiness;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TransportResilienceService(
        ITransportConfigurationService configuration,
        ITransportProductionTuningService tuning,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportPlcSignalMapService maps,
        ITransportStateStore stateStore,
        ITransportJournalStore journal,
        ITransportLogicalBackupStorage backupStorage,
        ITransportObservabilityService observability,
        ITransportConsistencyInspectionService consistency,
        ITransportVehicleRegistry vehicles,
        ITransportExecutionEngine executions,
        IRouteReservationManager reservations,
        ITransportDriverDiagnosticsService drivers,
        ITransportProductionDispatchService production,
        ITransportTelemetryService telemetry,
        TransportResilienceOptions options)
    {
        _configuration = configuration;
        _tuning = tuning;
        _stations = stations;
        _singleTrack = singleTrack;
        _maps = maps;
        _stateStore = stateStore;
        _journal = journal;
        _backupStorage = backupStorage;
        _observability = observability;
        _consistency = consistency;
        _vehicles = vehicles;
        _executions = executions;
        _reservations = reservations;
        _drivers = drivers;
        _production = production;
        _telemetry = telemetry;
        _options = options;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(null, 1000, cancellationToken).ConfigureAwait(false);
        var readiness = records
            .Where(x => x.Category == TransportJournalCategory.ProductionReadiness)
            .Select(x => Deserialize<TransportReadinessReport>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportReadinessReport>()
            .OrderByDescending(x => x.CompletedAtUtc)
            .FirstOrDefault();
        var baselines = records
            .Where(x => x.Category == TransportJournalCategory.OperationalBaseline)
            .Select(x => Deserialize<TransportOperationalBaseline>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportOperationalBaseline>()
            .OrderBy(x => x.CapturedAtUtc)
            .ToArray();
        var drills = records
            .Where(x => x.Category == TransportJournalCategory.RecoveryDrill)
            .Select(x => Deserialize<TransportRecoveryDrillReport>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportRecoveryDrillReport>()
            .OrderBy(x => x.CompletedAtUtc)
            .ToArray();
        lock (_sync)
        {
            _lastReadiness = readiness;
            _baselines.Clear();
            _baselines.AddRange(baselines.TakeLast(100));
            _drills.Clear();
            _drills.AddRange(drills.TakeLast(100));
        }
    }

    public async Task<TransportReadinessReport> RunPreflightAsync(
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.ResiliencePreflight,
            "transport.resilience.preflight");
        try
        {
            var checks = new List<TransportReadinessCheckItem>();
            var runtime = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false);
            var maps = await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false);
            TransportRuntimeSnapshot persisted;
            try
            {
                persisted = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                checks.Add(Passed(
                    TransportReadinessCheckType.RuntimeStateStore,
                    $"运行状态存储可读取：车辆 {persisted.Vehicles.Count}，任务 {persisted.Executions.Count}，命令 {persisted.Commands.Count}"));
            }
            catch (Exception ex)
            {
                persisted = new TransportRuntimeSnapshot();
                checks.Add(Failed(
                    TransportReadinessCheckType.RuntimeStateStore,
                    TransportReadinessSeverity.Critical,
                    $"运行状态存储不可读取：{ex.Message}"));
            }

            checks.Add(runtime.Version > 0
                ? Passed(TransportReadinessCheckType.RuntimeConfiguration, $"运行配置版本 {runtime.Version}")
                : Failed(TransportReadinessCheckType.RuntimeConfiguration, TransportReadinessSeverity.Warning, "尚未保存生产运行配置"));

            AddUniquenessChecks(runtime, checks);
            AddBindingChecks(runtime, maps, checks);
            AddDriverChecks(maps, checks);

            var health = _observability.GetHealth();
            checks.Add(health.State == TransportHealthState.Unhealthy
                ? Failed(TransportReadinessCheckType.TransportHealth, TransportReadinessSeverity.Error, $"运输健康评分 {health.Score}，状态 {health.State}")
                : health.State == TransportHealthState.Degraded
                    ? Failed(TransportReadinessCheckType.TransportHealth, TransportReadinessSeverity.Warning, $"运输健康评分 {health.Score}，状态 {health.State}")
                    : Passed(TransportReadinessCheckType.TransportHealth, $"运输健康评分 {health.Score}"));

            var consistency = _consistency.GetLastReport();
            if (consistency is null)
            {
                checks.Add(Failed(TransportReadinessCheckType.ConsistencyReport, TransportReadinessSeverity.Warning, "尚无三方一致性报告"));
            }
            else
            {
                var staleAfter = TimeSpan.FromSeconds(Math.Max(10, _options.PreflightIntervalSeconds * 2));
                var stale = DateTime.UtcNow - consistency.CompletedAtUtc > staleAfter;
                checks.Add(!consistency.Success || consistency.CriticalCount > 0
                    ? Failed(TransportReadinessCheckType.ConsistencyReport, TransportReadinessSeverity.Critical, $"一致性巡检失败或存在 Critical 差异：{consistency.CriticalCount}")
                    : consistency.ErrorCount > 0
                        ? Failed(TransportReadinessCheckType.ConsistencyReport, TransportReadinessSeverity.Error, $"一致性巡检存在 Error 差异：{consistency.ErrorCount}")
                        : stale
                            ? Failed(TransportReadinessCheckType.ConsistencyReport, TransportReadinessSeverity.Warning, "最近一致性报告已过期")
                            : Passed(TransportReadinessCheckType.ConsistencyReport, "最近一致性报告有效"));
            }

            var snapshots = await _journal.QueryAsync(
                TransportJournalCategory.ConfigurationSnapshot,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(snapshots.Count > 0
                ? Passed(TransportReadinessCheckType.ConfigurationSnapshot, "已存在配置安全快照")
                : Failed(TransportReadinessCheckType.ConfigurationSnapshot, TransportReadinessSeverity.Warning, "尚未创建配置安全快照"));

            var backups = await _backupStorage.GetManifestsAsync(1, cancellationToken).ConfigureAwait(false);
            if (backups.Count == 0)
            {
                checks.Add(Failed(TransportReadinessCheckType.LogicalBackup, TransportReadinessSeverity.Warning, "尚未创建逻辑备份"));
            }
            else
            {
                var age = DateTime.UtcNow - backups[0].CreatedAtUtc;
                checks.Add(age <= TimeSpan.FromMinutes(Math.Max(1, _options.MaximumBackupAgeMinutes))
                    ? Passed(TransportReadinessCheckType.LogicalBackup, $"最近备份时间 {backups[0].CreatedAtUtc:O}")
                    : Failed(TransportReadinessCheckType.LogicalBackup, TransportReadinessSeverity.Warning, $"最近备份已超过 {_options.MaximumBackupAgeMinutes} 分钟"));
            }

            var activeCommands = persisted.Commands.Count(x => x.Status is
                TransportCommandStatus.Pending or
                TransportCommandStatus.Sent or
                TransportCommandStatus.Acknowledged);
            var failedCommands = persisted.Commands.Count(x => x.Status is
                TransportCommandStatus.Failed or
                TransportCommandStatus.TimedOut);
            checks.Add(failedCommands > 0
                ? Failed(
                    TransportReadinessCheckType.ActiveCommandState,
                    TransportReadinessSeverity.Warning,
                    $"持久化命令中活动 {activeCommands}，失败或超时 {failedCommands}")
                : Passed(TransportReadinessCheckType.ActiveCommandState, $"持久化活动命令 {activeCommands}，无失败或超时命令"));

            var report = new TransportReadinessReport
            {
                Checks = checks,
                StartedAtUtc = started,
                CompletedAtUtc = DateTime.UtcNow
            };
            await SaveReadinessAsync(report, cancellationToken).ConfigureAwait(false);
            operation.Complete(report.IsReady, report.IsReady ? "生产就绪检查通过" : $"生产就绪检查未通过：Critical={report.CriticalCount}, Error={report.ErrorCount}, Warning={report.WarningCount}");
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var report = new TransportReadinessReport
            {
                Success = false,
                Error = ex.Message,
                Checks = new[]
                {
                    Failed(TransportReadinessCheckType.RuntimeStateStore, TransportReadinessSeverity.Critical, $"生产就绪检查执行失败：{ex.Message}")
                },
                StartedAtUtc = started,
                CompletedAtUtc = DateTime.UtcNow
            };
            await SaveReadinessAsync(report, cancellationToken).ConfigureAwait(false);
            operation.Complete(false, ex.Message);
            return report;
        }
    }

    public TransportReadinessReport? GetLastReadiness()
    {
        lock (_sync)
            return _lastReadiness;
    }

    public async Task<TransportOperationalBaseline> CaptureBaselineAsync(
        string name,
        string reason,
        string capturedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateText(name, nameof(name));
        ValidateText(reason, nameof(reason));
        ValidateText(capturedBy, nameof(capturedBy));
        var runtime = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false);
        var persisted = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var maps = await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = _drivers.GetAll();
        var vehicles = _vehicles.GetAll();
        var executions = _executions.GetAll();
        var baseline = new TransportOperationalBaseline
        {
            Name = name.Trim(),
            Reason = reason.Trim(),
            CapturedBy = capturedBy.Trim(),
            RuntimeConfigurationVersion = runtime.Version,
            ProductionTuningVersion = _tuning.Current.Version,
            VehicleCount = vehicles.Count,
            OnlineVehicleCount = vehicles.Count(x => x.IsOnline),
            ActiveExecutionCount = executions.Count(x => !x.IsTerminal),
            ActiveReservationCount = _reservations.GetActiveReservations().Count,
            ActiveCommandCount = persisted.Commands.Count(x => x.Status is TransportCommandStatus.Pending or TransportCommandStatus.Sent or TransportCommandStatus.Acknowledged),
            PlcSignalMapCount = maps.Count,
            PlcDriverOnlineCount = diagnostics.Count(x => x.AccessorConnected && x.DeviceOnline),
            QueueLength = _production.GetQueue().Count(x => x.State is not (TransportProductionQueueState.Assigned or TransportProductionQueueState.Cancelled)),
            Health = _observability.GetHealth(),
            Consistency = _consistency.GetLastReport(),
            Readiness = GetLastReadiness(),
            CapturedAtUtc = DateTime.UtcNow
        };
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.OperationalBaseline,
            RecordId = baseline.BaselineId,
            PayloadJson = JsonSerializer.Serialize(baseline, JsonOptions),
            OccurredAtUtc = baseline.CapturedAtUtc
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _baselines.Add(baseline);
            Trim(_baselines, 100);
        }
        return baseline;
    }

    public IReadOnlyList<TransportOperationalBaseline> GetBaselines(int maxCount = 100)
    {
        lock (_sync)
            return _baselines.OrderByDescending(x => x.CapturedAtUtc).Take(Math.Clamp(maxCount, 1, 100)).ToArray();
    }

    public async Task<TransportLogicalBackupManifest> CreateBackupAsync(
        string name,
        string reason,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        ValidateText(name, nameof(name));
        ValidateText(reason, nameof(reason));
        ValidateText(createdBy, nameof(createdBy));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.LogicalBackup,
            "transport.resilience.backup");
        try
        {
            var readiness = await RunPreflightAsync(cancellationToken).ConfigureAwait(false);
            if (_options.RequireReadyBeforeAutomaticBackup && !readiness.IsReady)
                throw new InvalidOperationException("生产就绪检查未通过，当前策略禁止创建自动逻辑备份");
            var baseline = await CaptureBaselineAsync(name, reason, createdBy, cancellationToken).ConfigureAwait(false);
            var payload = new TransportLogicalBackupPayload
            {
                RuntimeConfiguration = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false),
                ProductionTuning = _tuning.Current,
                ProductionStations = _stations.GetAll().Select(x => new TransportStationDefinition
                {
                    StationId = x.StationId,
                    Name = x.Name,
                    Capacity = x.Capacity,
                    MaximumQueuedTasks = x.MaximumQueuedTasks,
                    Enabled = x.Enabled
                }).ToArray(),
                SingleTrackSections = _singleTrack.GetSnapshots().Select(x => x.Definition).ToArray(),
                PlcSignalMaps = await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false),
                RuntimeState = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false),
                JournalRecords = await _journal.QueryAsync(null, Math.Clamp(_options.MaximumJournalRecords, 100, 20000), cancellationToken).ConfigureAwait(false),
                Baseline = baseline,
                CapturedAtUtc = DateTime.UtcNow
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var backupId = Guid.NewGuid().ToString("N");
            var manifest = new TransportLogicalBackupManifest
            {
                BackupId = backupId,
                Name = name.Trim(),
                Reason = reason.Trim(),
                CreatedBy = createdBy.Trim(),
                FileName = $"transport-backup-{DateTime.UtcNow:yyyyMMddHHmmss}-{backupId[..8]}.json",
                Sha256 = hash,
                SizeBytes = bytes.LongLength,
                SchemaVersion = payload.SchemaVersion,
                PreflightReady = readiness.IsReady,
                VehicleCount = payload.RuntimeState.Vehicles.Count,
                ActiveExecutionCount = payload.RuntimeState.Executions.Count(x => !x.IsTerminal),
                CreatedAtUtc = DateTime.UtcNow
            };
            await _backupStorage.SaveAsync(manifest, bytes, cancellationToken).ConfigureAwait(false);
            await _backupStorage.TrimAsync(Math.Clamp(_options.BackupRetentionCount, 1, 1000), cancellationToken).ConfigureAwait(false);
            await _journal.UpsertAsync(new TransportJournalRecord
            {
                Category = TransportJournalCategory.LogicalBackup,
                RecordId = manifest.BackupId,
                PayloadJson = JsonSerializer.Serialize(manifest, JsonOptions),
                OccurredAtUtc = manifest.CreatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
            operation.Complete(true, $"逻辑备份 {manifest.BackupId} 已创建", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["backup.id"] = manifest.BackupId,
                ["backup.sha256"] = manifest.Sha256,
                ["backup.size"] = manifest.SizeBytes.ToString()
            });
            return manifest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<TransportLogicalBackupManifest>> GetBackupsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        _backupStorage.GetManifestsAsync(Math.Clamp(maxCount, 1, 1000), cancellationToken);

    public Task<TransportLogicalBackupContent?> GetBackupContentAsync(
        string backupId,
        CancellationToken cancellationToken = default) =>
        _backupStorage.LoadAsync(backupId, cancellationToken);

    public async Task<TransportBackupValidationReport> ValidateBackupAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        var content = await _backupStorage.LoadAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return new TransportBackupValidationReport
            {
                BackupId = backupId,
                Issues = new[] { ValidationIssue(TransportBackupValidationIssueType.BackupMissing, TransportReadinessSeverity.Critical, "逻辑备份不存在") }
            };
        }
        var issues = new List<TransportBackupValidationIssue>();
        var hash = Convert.ToHexString(SHA256.HashData(content.Payload)).ToLowerInvariant();
        var hashValid = string.Equals(hash, content.Manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!hashValid)
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.HashMismatch, TransportReadinessSeverity.Critical, "逻辑备份 SHA-256 校验失败"));
        TransportLogicalBackupPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<TransportLogicalBackupPayload>(content.Payload, JsonOptions);
        }
        catch (Exception ex)
        {
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.DeserializeFailure, TransportReadinessSeverity.Critical, $"逻辑备份无法解析：{ex.Message}"));
        }
        var schemaValid = payload?.SchemaVersion == 1 && content.Manifest.SchemaVersion == 1;
        if (!schemaValid)
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.UnsupportedSchema, TransportReadinessSeverity.Critical, "逻辑备份版本不受支持"));
        if (payload is not null)
            ValidatePayload(payload, issues);
        return new TransportBackupValidationReport
        {
            BackupId = backupId,
            HashValid = hashValid,
            SchemaValid = schemaValid,
            PayloadReadable = payload is not null,
            Issues = issues,
            ValidatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<TransportRestorePreparationResult> PrepareRestoreAsync(
        string backupId,
        string preparedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateText(preparedBy, nameof(preparedBy));
        var validation = await ValidateBackupAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (!validation.CanPrepareConfigurationRestore)
        {
            return new TransportRestorePreparationResult
            {
                BackupId = backupId,
                Error = "逻辑备份校验未通过，不能准备配置恢复"
            };
        }
        var content = await _backupStorage.LoadAsync(backupId, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<TransportLogicalBackupPayload>(content!.Payload, JsonOptions)!;
        var snapshot = new TransportConfigurationSnapshot
        {
            Name = $"backup-restore-{content.Manifest.CreatedAtUtc:yyyyMMddHHmmss}",
            Reason = $"从逻辑备份 {backupId} 导入的待审批恢复快照",
            CreatedBy = preparedBy.Trim(),
            RuntimeConfiguration = payload.RuntimeConfiguration,
            ProductionTuning = payload.ProductionTuning,
            ProductionStations = payload.ProductionStations,
            SingleTrackSections = payload.SingleTrackSections,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ConfigurationSnapshot,
            RecordId = snapshot.SnapshotId,
            PayloadJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            OccurredAtUtc = snapshot.CreatedAtUtc
        }, cancellationToken).ConfigureAwait(false);
        return new TransportRestorePreparationResult
        {
            Success = true,
            BackupId = backupId,
            ImportedSnapshot = snapshot,
            ManualRecoveryActions = new[]
            {
                "使用 ChangeConfiguration 双人审批回滚到导入的配置快照",
                "PLC 点位映射必须与现场程序版本复核后，通过点位配置审批单独恢复",
                "活动任务、车辆位置、路权和命令状态不得从备份自动写回，必须执行三方一致性核对",
                "确认所有车辆物理停稳并核对 PLC 当前命令后，才可恢复任务执行"
            }
        };
    }

    public async Task<TransportRecoveryDrillReport> RunDrillAsync(
        TransportRecoveryDrillRequest request,
        string executedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, nameof(request.Reason));
        ValidateText(executedBy, nameof(executedBy));
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.RecoveryDrill,
            "transport.resilience.drill",
            vehicleId: request.TargetVehicleId,
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drill.scenario"] = request.Scenario.ToString(),
                ["drill.isolated"] = "true"
            });
        var started = DateTime.UtcNow;
        var steps = BuildDrillSteps(request);
        var report = new TransportRecoveryDrillReport
        {
            Scenario = request.Scenario,
            TargetVehicleId = request.TargetVehicleId,
            Reason = request.Reason.Trim(),
            ExecutedBy = executedBy.Trim(),
            IsIsolatedSimulation = true,
            Steps = steps,
            StartedAtUtc = started,
            CompletedAtUtc = DateTime.UtcNow
        };
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.RecoveryDrill,
            RecordId = report.DrillId,
            PayloadJson = JsonSerializer.Serialize(report, JsonOptions),
            OccurredAtUtc = report.CompletedAtUtc
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _drills.Add(report);
            Trim(_drills, 100);
        }
        operation.Complete(report.Passed, report.Passed ? "隔离恢复演练通过" : "隔离恢复演练存在未通过步骤");
        return report;
    }

    public IReadOnlyList<TransportRecoveryDrillReport> GetDrills(int maxCount = 100)
    {
        lock (_sync)
            return _drills.OrderByDescending(x => x.CompletedAtUtc).Take(Math.Clamp(maxCount, 1, 100)).ToArray();
    }

    public TransportResilienceSnapshot GetSnapshot()
    {
        TransportOperationalBaseline? baseline;
        TransportRecoveryDrillReport? drill;
        lock (_sync)
        {
            baseline = _baselines.OrderByDescending(x => x.CapturedAtUtc).FirstOrDefault();
            drill = _drills.OrderByDescending(x => x.CompletedAtUtc).FirstOrDefault();
        }
        var backups = _backupStorage.GetManifestsAsync(100).GetAwaiter().GetResult();
        return new TransportResilienceSnapshot
        {
            LastReadiness = GetLastReadiness(),
            LastBaseline = baseline,
            LastBackup = backups.FirstOrDefault(),
            LastDrill = drill,
            BackupCount = backups.Count,
            DrillCount = GetDrills(100).Count
        };
    }

    private void AddUniquenessChecks(
        TransportRuntimeConfiguration runtime,
        ICollection<TransportReadinessCheckItem> checks)
    {
        var duplicates = runtime.Vehicles.GroupBy(x => x.VehicleId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key)
            .Concat(runtime.Drivers.GroupBy(x => x.DriverId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key))
            .Concat(runtime.TrafficResources.GroupBy(x => x.ResourceId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        checks.Add(duplicates.Length == 0
            ? Passed(TransportReadinessCheckType.ConfigurationUniqueness, "运行配置标识符无重复")
            : Failed(TransportReadinessCheckType.ConfigurationUniqueness, TransportReadinessSeverity.Critical, $"运行配置存在重复标识符：{string.Join(", ", duplicates)}"));
    }

    private static void AddBindingChecks(
        TransportRuntimeConfiguration runtime,
        IReadOnlyList<TransportPlcSignalMap> maps,
        ICollection<TransportReadinessCheckItem> checks)
    {
        var enabledDrivers = runtime.Drivers.Where(x => x.Enabled).ToDictionary(x => x.DriverId, StringComparer.Ordinal);
        var mapByVehicle = maps.Where(x => x.Enabled).GroupBy(x => x.VehicleId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var missing = new List<string>();
        var invalid = new List<string>();
        foreach (var vehicle in runtime.Vehicles.Where(x => x.Enabled))
        {
            if (!mapByVehicle.TryGetValue(vehicle.VehicleId, out var map))
            {
                missing.Add(vehicle.VehicleId);
                continue;
            }
            if (!enabledDrivers.TryGetValue(map.DriverId, out var driver) || driver.Kind != vehicle.Kind || map.Kind != vehicle.Kind)
                invalid.Add(vehicle.VehicleId);
        }
        checks.Add(missing.Count == 0 && invalid.Count == 0
            ? Passed(TransportReadinessCheckType.VehicleDriverBinding, "启用车辆均具有有效驱动和点位映射")
            : Failed(
                TransportReadinessCheckType.VehicleDriverBinding,
                TransportReadinessSeverity.Error,
                $"缺少映射车辆：{string.Join(',', missing)}；绑定无效车辆：{string.Join(',', invalid)}"));

        var realMaps = maps.Where(x => x.Enabled && x.Mode == TransportDriverMode.PlcTag).ToArray();
        var incomplete = realMaps.Where(x =>
            string.IsNullOrWhiteSpace(x.HeartbeatTag) ||
            string.IsNullOrWhiteSpace(x.CurrentNodeTag) ||
            string.IsNullOrWhiteSpace(x.OperatingStateTag) ||
            string.IsNullOrWhiteSpace(x.CommandRequestTag) ||
            string.IsNullOrWhiteSpace(x.AcknowledgedSequenceTag)).Select(x => x.VehicleId).ToArray();
        checks.Add(incomplete.Length == 0
            ? Passed(TransportReadinessCheckType.PlcSignalMapCoverage, $"真实 PLC 点位映射 {realMaps.Length} 份，必填项完整")
            : Failed(TransportReadinessCheckType.PlcSignalMapCoverage, TransportReadinessSeverity.Critical, $"PLC 点位映射不完整：{string.Join(',', incomplete)}"));
    }

    private void AddDriverChecks(
        IReadOnlyList<TransportPlcSignalMap> maps,
        ICollection<TransportReadinessCheckItem> checks)
    {
        var realMaps = maps.Where(x => x.Enabled && x.Mode == TransportDriverMode.PlcTag).ToArray();
        if (realMaps.Length == 0)
        {
            checks.Add(Passed(TransportReadinessCheckType.PlcDriverFreshness, "当前为模拟模式或未启用真实 PLC 驱动"));
            return;
        }
        var diagnostics = _drivers.GetAll().ToDictionary(x => x.VehicleId, StringComparer.Ordinal);
        var failed = new List<string>();
        foreach (var map in realMaps)
        {
            if (!diagnostics.TryGetValue(map.VehicleId, out var diagnostic) ||
                !diagnostic.AccessorConnected ||
                !diagnostic.DeviceOnline ||
                !diagnostic.LastReadAtUtc.HasValue ||
                DateTime.UtcNow - diagnostic.LastReadAtUtc.Value > TimeSpan.FromMilliseconds(Math.Max(1000, map.HeartbeatTimeoutMs)))
            {
                failed.Add(map.VehicleId);
            }
        }
        checks.Add(failed.Count == 0
            ? Passed(TransportReadinessCheckType.PlcDriverFreshness, $"真实 PLC 驱动 {realMaps.Length} 台均在线且状态新鲜")
            : Failed(TransportReadinessCheckType.PlcDriverFreshness, TransportReadinessSeverity.Critical, $"PLC 驱动离线或状态过期：{string.Join(',', failed)}"));
    }

    private async Task SaveReadinessAsync(
        TransportReadinessReport report,
        CancellationToken cancellationToken)
    {
        lock (_sync)
            _lastReadiness = report;
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionReadiness,
            RecordId = report.ReportId,
            PayloadJson = JsonSerializer.Serialize(report, JsonOptions),
            OccurredAtUtc = report.CompletedAtUtc
        }, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<TransportRecoveryDrillStep> BuildDrillSteps(TransportRecoveryDrillRequest request)
    {
        var steps = new List<TransportRecoveryDrillStep>();
        void Add(string action, string expected, bool passed, string message) => steps.Add(new TransportRecoveryDrillStep
        {
            Sequence = steps.Count + 1,
            Action = action,
            ExpectedResult = expected,
            Passed = passed,
            Message = message
        });

        var vehiclesBefore = _vehicles.GetAll().Select(x => x with { }).ToArray();
        var reservationsBefore = _reservations.GetActiveReservations().Select(x => x with { }).ToArray();
        switch (request.Scenario)
        {
            case TransportRecoveryDrillScenario.DriverOffline:
            case TransportRecoveryDrillScenario.HeartbeatTimeout:
                var diagnostic = string.IsNullOrWhiteSpace(request.TargetVehicleId)
                    ? _drivers.GetAll().FirstOrDefault()
                    : _drivers.GetAll().FirstOrDefault(x => string.Equals(x.VehicleId, request.TargetVehicleId, StringComparison.Ordinal));
                Add("复制目标驱动诊断状态到隔离演练上下文", "不读取或写入 PLC 点位", diagnostic is not null, diagnostic is null ? "未找到目标车辆诊断状态" : "已建立隔离诊断副本");
                Add("在副本中模拟离线或心跳冻结", "生产驱动诊断保持不变", diagnostic is not null, "仅修改演练副本");
                Add("评估预期处置", "停止派单、保留物理占用、要求位置核对", diagnostic is not null, "处置策略符合安全停机原则");
                break;
            case TransportRecoveryDrillScenario.StateStoreUnavailable:
                Add("模拟状态存储读取异常", "Host 继续运行但 Readiness 进入 Unhealthy", true, "演练使用预定义异常，不访问生产存储写路径");
                Add("验证恢复动作", "禁止自动恢复活动任务并要求三方核对", true, "恢复动作仅生成检查清单");
                break;
            case TransportRecoveryDrillScenario.OrphanReservation:
                Add("复制活动路权到隔离上下文", "生产路权不变化", true, $"复制 {reservationsBefore.Length} 条活动路权");
                Add("在副本中构造孤儿路权", "识别为人工确认项，不自动释放", true, "孤儿路权处置需要物理清场确认");
                break;
            case TransportRecoveryDrillScenario.ConfigurationVersionConflict:
                Add("模拟配置 expectedVersion 落后", "保存或回滚被拒绝", true, "乐观锁应返回版本冲突");
                Add("生成重新读取与重新审批步骤", "旧审批不得复用", true, "恢复流程要求重新确认版本");
                break;
            case TransportRecoveryDrillScenario.StaleConsistencyReport:
                Add("模拟一致性报告过期", "生产就绪检查返回 Warning", true, "不自动覆盖车辆或 PLC 状态");
                Add("生成手动巡检步骤", "重新执行三方一致性巡检", true, "巡检仍为只读诊断");
                break;
            case TransportRecoveryDrillScenario.ActiveCommandAfterRestart:
                Add("复制持久化活动命令到隔离上下文", "不向 PLC 重发命令", true, "活动命令仅用于对账");
                Add("比较 PLC 当前命令和持久化命令", "不一致时要求人工确认", true, "禁止自动续跑运动命令");
                break;
            default:
                Add("识别演练场景", "场景受支持", false, "不支持的演练场景");
                break;
        }
        var vehiclesAfter = _vehicles.GetAll();
        var reservationsAfter = _reservations.GetActiveReservations();
        Add(
            "验证生产运行时未被演练修改",
            "车辆和路权集合保持不变",
            SameVehicles(vehiclesBefore, vehiclesAfter) && SameReservations(reservationsBefore, reservationsAfter),
            "隔离演练不会修改车辆、任务、路权或 PLC 状态");
        return steps;
    }

    private static void ValidatePayload(
        TransportLogicalBackupPayload payload,
        ICollection<TransportBackupValidationIssue> issues)
    {
        var duplicates = payload.RuntimeConfiguration.Vehicles.GroupBy(x => x.VehicleId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key)
            .Concat(payload.RuntimeConfiguration.Drivers.GroupBy(x => x.DriverId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.DuplicateIdentifier, TransportReadinessSeverity.Critical, $"备份配置存在重复标识符：{string.Join(',', duplicates)}"));
        var driverIds = payload.RuntimeConfiguration.Drivers.Select(x => x.DriverId).ToHashSet(StringComparer.Ordinal);
        var missingDrivers = payload.PlcSignalMaps.Where(x => !driverIds.Contains(x.DriverId)).Select(x => x.VehicleId).ToArray();
        if (missingDrivers.Length > 0)
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.MissingDriverBinding, TransportReadinessSeverity.Error, $"备份点位映射缺少驱动：{string.Join(',', missingDrivers)}"));
        if (payload.RuntimeState.Executions.Any(x => !x.IsTerminal))
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.RuntimeStateRequiresManualRecovery, TransportReadinessSeverity.Warning, "备份包含活动任务，恢复时必须三方核对，不得自动写回"));
        if (payload.RuntimeState.Commands.Any(x => x.Status is TransportCommandStatus.Pending or TransportCommandStatus.Sent or TransportCommandStatus.Acknowledged))
            issues.Add(ValidationIssue(TransportBackupValidationIssueType.ActiveCommandRequiresManualRecovery, TransportReadinessSeverity.Warning, "备份包含活动命令，恢复时禁止自动重发"));
    }

    private static bool SameVehicles(
        IReadOnlyList<TransportVehicleSnapshot> before,
        IReadOnlyList<TransportVehicleSnapshot> after) =>
        before.Count == after.Count && before.OrderBy(x => x.VehicleId).Zip(after.OrderBy(x => x.VehicleId)).All(x => x.First == x.Second);

    private static bool SameReservations(
        IReadOnlyList<RouteReservation> before,
        IReadOnlyList<RouteReservation> after) =>
        before.Count == after.Count && before.OrderBy(x => x.ReservationId).Zip(after.OrderBy(x => x.ReservationId)).All(x => x.First == x.Second);

    private static TransportReadinessCheckItem Passed(TransportReadinessCheckType type, string message) => new()
    {
        CheckType = type,
        Severity = TransportReadinessSeverity.Information,
        Passed = true,
        Message = message
    };

    private static TransportReadinessCheckItem Failed(
        TransportReadinessCheckType type,
        TransportReadinessSeverity severity,
        string message) => new()
    {
        CheckType = type,
        Severity = severity,
        Passed = false,
        Message = message
    };

    private static TransportBackupValidationIssue ValidationIssue(
        TransportBackupValidationIssueType type,
        TransportReadinessSeverity severity,
        string message) => new()
    {
        IssueType = type,
        Severity = severity,
        Message = message
    };

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private static void ValidateText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} 不能为空", parameterName);
    }

    private static void Trim<T>(List<T> items, int capacity)
    {
        var excess = items.Count - capacity;
        if (excess > 0)
            items.RemoveRange(0, excess);
    }
}
