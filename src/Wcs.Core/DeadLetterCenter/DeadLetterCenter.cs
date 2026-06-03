namespace Wcs.Core.DeadLetterCenter;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// 死信中心实现 — 统一管理所有失败/异常记录
///
/// 替代方案：原本异常直接 throw 或 log 后无人追踪，
/// 现在所有失败记录进入死信中心，可查询、可统计、可标记处理。
/// </summary>
public class DeadLetterCenter : IDeadLetterCenter
{
    private readonly ConcurrentDictionary<string, DeadLetterRecord> _records = new();
    private readonly ILogger<DeadLetterCenter>? _logger;
    private const int MaxRecords = 10000;

    public DeadLetterCenter(ILogger<DeadLetterCenter>? logger = null)
    {
        _logger = logger;
    }

    public string Post(DeadLetterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records[record.Id] = record;

        // 达到上限时移除最早的 20%
        if (_records.Count > MaxRecords)
        {
            var toRemove = _records.Values
                .OrderBy(r => r.CreatedTime)
                .Take(MaxRecords / 5)
                .Select(r => r.Id)
                .ToList();

            foreach (var id in toRemove)
                _records.TryRemove(id, out _);
        }

        _logger?.LogWarning(
            "DeadLetter [{Type}] {Summary} (Source={Source}, OriginalId={OriginalId})",
            record.Type, record.Summary, record.SourceModule, record.OriginalId);

        return record.Id;
    }

    public string PostQuick(DeadLetterType type, string sourceModule, string summary,
        string? originalId = null, string? deviceId = null, string? detail = null)
    {
        return Post(new DeadLetterRecord
        {
            Type = type,
            SourceModule = sourceModule,
            Summary = summary,
            OriginalId = originalId,
            DeviceId = deviceId,
            Detail = detail
        });
    }

    public DeadLetterRecord? Get(string id)
    {
        _records.TryGetValue(id, out var record);
        return record;
    }

    public IEnumerable<DeadLetterRecord> Query(DeadLetterType? type = null,
        string? sourceModule = null, string? deviceId = null, int maxResults = 100)
    {
        var query = _records.Values.AsEnumerable();

        if (type.HasValue)
            query = query.Where(r => r.Type == type.Value);
        if (!string.IsNullOrEmpty(sourceModule))
            query = query.Where(r => r.SourceModule == sourceModule);
        if (!string.IsNullOrEmpty(deviceId))
            query = query.Where(r => r.DeviceId == deviceId);

        return query.OrderByDescending(r => r.CreatedTime).Take(maxResults).ToList();
    }

    public bool Resolve(string id, string resolvedBy)
    {
        if (!_records.TryGetValue(id, out var record))
            return false;

        record.IsResolved = true;
        record.ResolvedBy = resolvedBy;
        record.ResolvedTime = DateTime.UtcNow;
        return true;
    }

    public int GetUnresolvedCount()
    {
        return _records.Values.Count(r => !r.IsResolved);
    }

    public DeadLetterStats GetStats()
    {
        var all = _records.Values;
        return new DeadLetterStats
        {
            TotalRecords = all.Count,
            UnresolvedCount = all.Count(r => !r.IsResolved),
            TaskFailures = all.Count(r => r.Type is DeadLetterType.TaskGenerationFailed or DeadLetterType.TaskExecutionFailed),
            CommandTimeouts = all.Count(r => r.Type == DeadLetterType.CommandTimeout),
            RouteFailures = all.Count(r => r.Type == DeadLetterType.RouteFailed),
            DeviceFaults = all.Count(r => r.Type == DeadLetterType.DeviceFault)
        };
    }

    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var toRemove = _records.Values
            .Where(r => r.IsResolved && r.ResolvedTime.HasValue && r.ResolvedTime.Value < cutoff)
            .Select(r => r.Id)
            .ToList();

        foreach (var id in toRemove)
            _records.TryRemove(id, out _);

        return toRemove.Count;
    }
}
