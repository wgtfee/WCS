namespace Wcs.Core.PlcSubsystem;

using System.Collections.Concurrent;

/// <summary>
/// PLC 连接状态枚举
/// </summary>
public enum PlcConnectionStatusEnum
{
    /// <summary>
    /// 未连接
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// 连接中
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// 已连接
    /// </summary>
    Connected = 2,

    /// <summary>
    /// 连接失败
    /// </summary>
    Failed = 3,

    /// <summary>
    /// 断开连接中
    /// </summary>
    Disconnecting = 4
}

/// <summary>
/// S7 连接状态
/// </summary>
public class PlcConnectionStatus
{
    public string PlcName { get; set; } = string.Empty;

    public PlcConnectionStatusEnum Status { get; set; }

    public DateTime LastConnectTime { get; set; }

    public DateTime LastHeartbeat { get; set; }

    public int FailureCount { get; set; }

    public string? LastError { get; set; }

    public int ReadCount { get; set; }

    public int WriteCount { get; set; }
}

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
