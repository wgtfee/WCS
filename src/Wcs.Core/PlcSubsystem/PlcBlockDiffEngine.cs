namespace Wcs.Core.PlcSubsystem;

using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// PLC 块数据变化
/// </summary>
public class PlcBlockChange
{
    public int Offset { get; set; }

    public byte OldValue { get; set; }

    public byte NewValue { get; set; }

    public override string ToString()
    {
        return $"Offset:{Offset} {OldValue:X2}→{NewValue:X2}";
    }
}

/// <summary>
/// PLC 块数据对比结果
/// </summary>
public class PlcBlockDiff
{
    public string PlcName { get; set; } = string.Empty;

    public int BlockNumber { get; set; }

    public byte[] OldData { get; set; } = Array.Empty<byte>();

    public byte[] NewData { get; set; } = Array.Empty<byte>();

    public List<PlcBlockChange> Changes { get; set; } = new();

    public DateTime CompareTime { get; set; }

    public bool HasChanges => Changes.Count > 0;

    public int ChangeCount => Changes.Count;

    public override string ToString()
    {
        return $"PLC:{PlcName} Block:{BlockNumber} Changes:{ChangeCount}";
    }
}

/// <summary>
/// PLC 块数据对比引擎
/// </summary>
public interface IPlcBlockDiffEngine
{
    /// <summary>
    /// 对比两个块数据
    /// </summary>
    PlcBlockDiff ComparePlcBlocks(PlcBlock oldBlock, PlcBlock newBlock);

    /// <summary>
    /// 对比多个块
    /// </summary>
    IEnumerable<PlcBlockDiff> CompareMultipleBlocks(IEnumerable<(PlcBlock Old, PlcBlock New)> blockPairs);

    /// <summary>
    /// 获取上一次读取的块数据
    /// </summary>
    PlcBlock? GetLastBlock(string plcName, int blockNumber);

    /// <summary>
    /// 设置上一次读取的块数据
    /// </summary>
    void SetLastBlock(PlcBlock block);

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    void ClearCache();

    IEnumerable<PlcBlock> GetCachedBlocks();

    int GetCachedBlockCount();
}

/// <summary>
/// PLC 块数据对比引擎实现
/// </summary>
public class PlcBlockDiffEngine : IPlcBlockDiffEngine
{
    private readonly ConcurrentDictionary<string, PlcBlock> _lastBlocks = new();

    /// <summary>
    /// 对比算法 - CRC32 哈希预检 + 逐字节对比（二级检测）
    /// 先比较 CRC32 哈希，不同才做逐字节精确对比
    /// </summary>
    public PlcBlockDiff ComparePlcBlocks(PlcBlock oldBlock, PlcBlock newBlock)
    {
        ArgumentNullException.ThrowIfNull(oldBlock);
        ArgumentNullException.ThrowIfNull(newBlock);

        // === V4 优化：CRC32 哈希预检 ===
        // 如果两个块都有 CRC32 且相同，跳过逐字节对比
        if (oldBlock.Crc32 != 0 && newBlock.Crc32 != 0 && oldBlock.Crc32 == newBlock.Crc32)
        {
            return new PlcBlockDiff
            {
                PlcName = newBlock.PlcName,
                BlockNumber = newBlock.BlockNumber,
                OldData = (byte[])oldBlock.Data.Clone(),
                NewData = (byte[])newBlock.Data.Clone(),
                CompareTime = DateTime.UtcNow
                // Changes 为空 → HasChanges = false
            };
        }

        var diff = new PlcBlockDiff
        {
            PlcName = newBlock.PlcName,
            BlockNumber = newBlock.BlockNumber,
            OldData = (byte[])oldBlock.Data.Clone(),
            NewData = (byte[])newBlock.Data.Clone(),
            CompareTime = DateTime.UtcNow
        };

        // 确保两个块大小相同
        var minLength = Math.Min(oldBlock.Data.Length, newBlock.Data.Length);
        var maxLength = Math.Max(oldBlock.Data.Length, newBlock.Data.Length);

        // 对比相同部分
        for (int i = 0; i < minLength; i++)
        {
            if (oldBlock.Data[i] != newBlock.Data[i])
            {
                diff.Changes.Add(new PlcBlockChange
                {
                    Offset = i,
                    OldValue = oldBlock.Data[i],
                    NewValue = newBlock.Data[i]
                });
            }
        }

        // 处理长度差异
        if (oldBlock.Data.Length != newBlock.Data.Length)
        {
            // 如果新数据更长，记录新增的字节
            if (newBlock.Data.Length > oldBlock.Data.Length)
            {
                for (int i = minLength; i < newBlock.Data.Length; i++)
                {
                    diff.Changes.Add(new PlcBlockChange
                    {
                        Offset = i,
                        OldValue = 0,
                        NewValue = newBlock.Data[i]
                    });
                }
            }
        }

        return diff;
    }

    public IEnumerable<PlcBlockDiff> CompareMultipleBlocks(IEnumerable<(PlcBlock Old, PlcBlock New)> blockPairs)
    {
        ArgumentNullException.ThrowIfNull(blockPairs);

        var results = new List<PlcBlockDiff>();

        foreach (var (oldBlock, newBlock) in blockPairs)
        {
            var diff = ComparePlcBlocks(oldBlock, newBlock);
            results.Add(diff);
        }

        return results;
    }

    public PlcBlock? GetLastBlock(string plcName, int blockNumber)
    {
        ArgumentNullException.ThrowIfNull(plcName);

        var key = GetBlockKey(plcName, blockNumber);
        _lastBlocks.TryGetValue(key, out var block);
        return block;
    }

    public void SetLastBlock(PlcBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var key = GetBlockKey(block.PlcName, block.BlockNumber);
        _lastBlocks.AddOrUpdate(key, block, (_, _) => block);
    }

    public void ClearCache()
    {
        _lastBlocks.Clear();
    }

    private static string GetBlockKey(string plcName, int blockNumber)
    {
        return $"{plcName}:{blockNumber}";
    }

    /// <summary>
    /// 获取缓存的块数据
    /// </summary>
    public IEnumerable<PlcBlock> GetCachedBlocks()
    {
        return _lastBlocks.Values.ToList();
    }

    /// <summary>
    /// 获取缓存的块数量
    /// </summary>
    public int GetCachedBlockCount()
    {
        return _lastBlocks.Count;
    }
}

/// <summary>
/// PLC 块变化处理器
/// </summary>
public interface IPlcBlockChangeHandler
{
    /// <summary>
    /// 处理块变化
    /// </summary>
    Task HandleBlockChangeAsync(PlcBlockDiff diff, CancellationToken cancellationToken = default);
}

/// <summary>
/// PLC 块变化事件发布器
/// </summary>
public class PlcBlockChangePublisher
{
    private readonly List<IPlcBlockChangeHandler> _handlers = new();

    public void Subscribe(IPlcBlockChangeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
    }

    public void Unsubscribe(IPlcBlockChangeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Remove(handler);
    }

    public async Task PublishAsync(PlcBlockDiff diff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var tasks = _handlers.Select(h => h.HandleBlockChangeAsync(diff, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public int GetSubscriberCount()
    {
        return _handlers.Count;
    }
}
