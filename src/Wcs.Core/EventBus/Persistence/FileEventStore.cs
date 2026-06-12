namespace Wcs.Core.EventBus.Persistence;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.EventBus.Events;

/// <summary>
/// 基于文件系统的事件存储 — JSON-lines 格式，按小时轮转文件
/// 线程安全：内存缓冲 + 后台批量刷盘，查询时自动等待未完成写入
/// </summary>
public class FileEventStore : IEventStore, IDisposable
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentQueue<string> _buffer = new();
    private readonly SemaphoreSlim _flushSignal = new(0);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private int _pendingCount;
    private bool _disposed;

    private const string FilePrefix = "events_";
    private const string FileExtension = ".jsonl";
    private const int FlushBatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    public FileEventStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eventstore");
        Directory.CreateDirectory(_storagePath);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        _flushTimer = new Timer(_ => _flushSignal.Release(), null, FlushInterval, FlushInterval);
        _flushTask = FlushLoopAsync(_cts.Token);
    }

    public async Task AppendAsync(IEvent @event, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new EventRecord
        {
            EventId = @event.EventId,
            EventType = @event.GetType().AssemblyQualifiedName ?? @event.GetType().FullName ?? "",
            OccurTime = @event.OccurTime,
            Priority = @event.Priority,
            Source = @event.Source,
            Payload = JsonSerializer.SerializeToElement(@event, @event.GetType(), _jsonOptions)
        }, _jsonOptions);

        _buffer.Enqueue(json);
        Interlocked.Increment(ref _pendingCount);

        // 累积到批量大小时立即触发刷盘
        if (_pendingCount >= FlushBatchSize)
            _flushSignal.Release();

        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<IEvent>> QueryAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        await FlushPendingAsync(ct);
        return await ReadEventsInRangeAsync(from, to, ct);
    }

    public async Task<IReadOnlyList<IEvent>> GetLatestAsync(int count, CancellationToken ct = default)
    {
        await FlushPendingAsync(ct);

        var files = Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f)
            .ToList();

        var events = new List<IEvent>();
        var currentFile = GetCurrentFilePath();
        foreach (var file in files)
        {
            if (events.Count >= count) break;

            string[] lines;
            if (string.Equals(file, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                // 当前文件可能正在写入，加锁读取
                await _fileLock.WaitAsync(ct);
                try
                {
                    try { lines = await File.ReadAllLinesAsync(file, ct); }
                    catch (IOException) { await Task.Delay(100, ct); lines = await File.ReadAllLinesAsync(file, ct); }
                }
                finally { _fileLock.Release(); }
            }
            else
            {
                lines = await File.ReadAllLinesAsync(file, ct);
            }

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (events.Count >= count) break;
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var evt = DeserializeEvent(lines[i]);
                if (evt != null) events.Add(evt);
            }
        }

        events.Reverse();
        return events;
    }

    public async Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await FlushPendingAsync(ct);

        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var removed = 0;

        foreach (var file in Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}"))
        {
            if (ct.IsCancellationRequested) break;

            var fileName = Path.GetFileNameWithoutExtension(file);
            var ts = fileName[FilePrefix.Length..];

            if (DateTime.TryParseExact(ts, "yyyyMMddHH", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var fileTime))
            {
                if (fileTime < cutoff)
                {
                    try { File.Delete(file); removed++; }
                    catch { /* best effort */ }
                }
            }
        }

        return removed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _flushTimer.Dispose();
        _flushSignal.Dispose();
        FlushPendingSync();
    }

    // ==================== 内部 ====================

    private string GetCurrentFilePath()
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMddHH");
        return Path.Combine(_storagePath, $"{FilePrefix}{ts}{FileExtension}");
    }

    /// <summary>
    /// 后台刷盘循环
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _flushSignal.WaitAsync(ct);
                await FlushBatchAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 将缓冲区中的事件批量写入文件
    /// </summary>
    private async Task FlushBatchAsync(CancellationToken ct)
    {
        if (_pendingCount == 0) return;

        var batch = new List<string>(FlushBatchSize);
        while (_buffer.TryDequeue(out var line) && batch.Count < FlushBatchSize)
        {
            batch.Add(line);
        }

        if (batch.Count == 0) return;

        Interlocked.Add(ref _pendingCount, -batch.Count);

        var filePath = GetCurrentFilePath();
        await _fileLock.WaitAsync(ct);
        try
        {
            await File.AppendAllLinesAsync(filePath, batch, ct);
        }
        catch (IOException)
        {
            // 并发写入冲突时退避重试 (跨进程)
            await Task.Delay(100, ct);
            await File.AppendAllLinesAsync(filePath, batch, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 等待所有待写入完成（查询前调用）
    /// </summary>
    private async Task FlushPendingAsync(CancellationToken ct)
    {
        // 循环 flush 直到缓冲清空
        while (_pendingCount > 0 && !ct.IsCancellationRequested)
        {
            await FlushBatchAsync(ct);
        }
    }

    /// <summary>
    /// 同步刷盘（Dispose 时调用）
    /// </summary>
    private void FlushPendingSync()
    {
        var batch = new List<string>();
        while (_buffer.TryDequeue(out var line))
            batch.Add(line);

        if (batch.Count == 0) return;
        _pendingCount = 0;

        var filePath = GetCurrentFilePath();
        _fileLock.Wait();
        try
        {
            try
            {
                File.AppendAllLines(filePath, batch);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
                File.AppendAllLines(filePath, batch);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<IReadOnlyList<IEvent>> ReadEventsInRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var events = new List<IEvent>();

        var fromHour = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        var toHour = new DateTime(to.Year, to.Month, to.Day, to.Hour, 0, 0, DateTimeKind.Utc);

        for (var h = fromHour; h <= toHour; h = h.AddHours(1))
        {
            var fileName = $"{FilePrefix}{h:yyyyMMddHH}{FileExtension}";
            var filePath = Path.Combine(_storagePath, fileName);

            if (!File.Exists(filePath)) continue;

            string[] lines;
            await _fileLock.WaitAsync(ct);
            try
            {
                try
                {
                    lines = await File.ReadAllLinesAsync(filePath, ct);
                }
                catch (IOException)
                {
                    await Task.Delay(100, ct);
                    lines = await File.ReadAllLinesAsync(filePath, ct);
                }
            }
            finally
            {
                _fileLock.Release();
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var evt = DeserializeEvent(line);
                if (evt != null && evt.OccurTime >= from && evt.OccurTime <= to)
                {
                    events.Add(evt);
                }
            }
        }

        return events;
    }

    private IEvent? DeserializeEvent(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<EventRecord>(line, _jsonOptions);
            if (record?.Payload == null || string.IsNullOrEmpty(record.EventType))
                return null;

            var type = Type.GetType(record.EventType);
            if (type == null || !typeof(IEvent).IsAssignableFrom(type))
                return null;

            var evt = JsonSerializer.Deserialize(record.Payload.Value.GetRawText(), type, _jsonOptions);
            return evt as IEvent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 事件存储记录 — 包含类型信息用于反序列化
    /// </summary>
    private record EventRecord
    {
        public string EventId { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public DateTime OccurTime { get; init; }
        public EventPriority Priority { get; init; }
        public string Source { get; init; } = string.Empty;
        public JsonElement? Payload { get; init; }
    }
}
