namespace Wcs.Core.PlcSubsystem;

using System.Collections.Concurrent;
using Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>
/// PLC 块数据
/// </summary>
public class PlcBlock
{
    public string PlcName { get; set; } = string.Empty;

    public int BlockNumber { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();

    public DateTime ReadTime { get; set; }

    public bool IsValid { get; set; }

    /// <summary>CRC32 哈希 — 用于快速判断块数据是否变化</summary>
    public uint Crc32 { get; set; }
}

/// <summary>
/// CRC32 计算工具 — 用于 PLC 块数据快速哈希比对
/// </summary>
public static class Crc32Helper
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int j = 0; j < 8; j++)
            {
                if ((c & 1) != 0)
                    c = 0xEDB88320 ^ (c >> 1);
                else
                    c >>= 1;
            }
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// 计算字节数组的 CRC32 校验值
    /// </summary>
    public static uint Compute(byte[] data)
    {
        if (data == null || data.Length == 0)
            return 0;

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// 从 PlcBlock 数据创建带 CRC32 的块
    /// </summary>
    public static PlcBlock WithHash(PlcBlock block)
    {
        block.Crc32 = Compute(block.Data);
        return block;
    }
}

/// <summary>
/// S7 连接接口
/// </summary>
public interface IS7Connection
{
    /// <summary>
    /// 连接到 PLC
    /// </summary>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开与 PLC 的连接
    /// </summary>
    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取 PLC 块数据
    /// </summary>
    Task<byte[]?> ReadBlockAsync(int blockNumber, int length, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向 PLC 写入块数据
    /// </summary>
    Task<bool> WriteBlockAsync(int blockNumber, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取连接状态
    /// </summary>
    PlcConnectionStatus GetStatus();

    /// <summary>
    /// PLC 连接名称
    /// </summary>
    string PlcName { get; }

    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }
}

/// <summary>
/// S7 连接配置
/// </summary>
public class S7ConnectionConfig
{
    public string PlcName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public int Rack { get; set; } = 0;

    public int Slot { get; set; } = 1;

    public int Timeout { get; set; } = 5000;

    public int RetryCount { get; set; } = 3;

    public int RetryInterval { get; set; } = 1000;
}

/// <summary>
/// S7 连接实现 - 基于模拟实现（生产环境应使用真实 S7Client）
/// </summary>
public class S7Connection : IS7Connection
{
    private readonly S7ConnectionConfig _config;
    private readonly PlcConnectionStatus _status;
    private readonly object _lockObj = new();
    private bool _isConnected;
    private readonly ConcurrentDictionary<int, byte[]> _blockCache = new();

    public string PlcName => _config.PlcName;

    public bool IsConnected => _isConnected;

    public S7Connection(S7ConnectionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _status = new PlcConnectionStatus
        {
            PlcName = config.PlcName,
            Status = PlcConnectionStatusEnum.Disconnected
        };
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObj)
        {
            if (_isConnected)
                return true;

            _status.Status = PlcConnectionStatusEnum.Connecting;
        }

        try
        {
            // 模拟连接延迟
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            lock (_lockObj)
            {
                _isConnected = true;
                _status.Status = PlcConnectionStatusEnum.Connected;
                _status.LastConnectTime = DateTime.UtcNow;
                _status.LastHeartbeat = DateTime.UtcNow;
                _status.FailureCount = 0;
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (_lockObj)
            {
                _isConnected = false;
                _status.Status = PlcConnectionStatusEnum.Failed;
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }

            return false;
        }
    }

    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObj)
        {
            if (!_isConnected)
                return true;

            _status.Status = PlcConnectionStatusEnum.Disconnecting;
        }

        try
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            lock (_lockObj)
            {
                _isConnected = false;
                _status.Status = PlcConnectionStatusEnum.Disconnected;
                _blockCache.Clear();
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (_lockObj)
            {
                _status.LastError = ex.Message;
            }

            return false;
        }
    }

    public async Task<byte[]?> ReadBlockAsync(int blockNumber, int length, CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
            return null;

        try
        {
            // 模拟读取延迟
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);

            // 如果缓存中有数据，返回缓存（模拟）
            if (_blockCache.TryGetValue(blockNumber, out var cachedData) && cachedData.Length == length)
            {
                lock (_lockObj)
                {
                    _status.ReadCount++;
                    _status.LastHeartbeat = DateTime.UtcNow;
                }

                return cachedData;
            }

            // 生成模拟数据
            var data = new byte[length];
            new Random(blockNumber).NextBytes(data);

            _blockCache.AddOrUpdate(blockNumber, data, (_, _) => data);

            lock (_lockObj)
            {
                _status.ReadCount++;
                _status.LastHeartbeat = DateTime.UtcNow;
            }

            return data;
        }
        catch (Exception ex)
        {
            lock (_lockObj)
            {
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }

            return null;
        }
    }

    public async Task<bool> WriteBlockAsync(int blockNumber, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
            return false;

        ArgumentNullException.ThrowIfNull(data);

        try
        {
            // 模拟写入延迟
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);

            _blockCache.AddOrUpdate(blockNumber, data, (_, _) => (byte[])data.Clone());

            lock (_lockObj)
            {
                _status.WriteCount++;
                _status.LastHeartbeat = DateTime.UtcNow;
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (_lockObj)
            {
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }

            return false;
        }
    }

    public PlcConnectionStatus GetStatus()
    {
        lock (_lockObj)
        {
            return new PlcConnectionStatus
            {
                PlcName = _status.PlcName,
                Status = _status.Status,
                LastConnectTime = _status.LastConnectTime,
                LastHeartbeat = _status.LastHeartbeat,
                FailureCount = _status.FailureCount,
                LastError = _status.LastError,
                ReadCount = _status.ReadCount,
                WriteCount = _status.WriteCount
            };
        }
    }
}
