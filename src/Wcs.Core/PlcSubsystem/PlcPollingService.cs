namespace Wcs.Core.PlcSubsystem;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLC 轮询服务接口
/// </summary>
public interface IPlcPollingService
{
    /// <summary>
    /// 添加 PLC 连接
    /// </summary>
    void AddPlcConnection(S7Connection connection, int pollingIntervalMs = 100);

    /// <summary>
    /// 移除 PLC 连接
    /// </summary>
    void RemovePlcConnection(string plcName);

    /// <summary>
    /// 启动轮询
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止轮询
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 PLC 连接状态
    /// </summary>
    PlcConnectionStatus? GetConnectionStatus(string plcName);

    /// <summary>
    /// 获取所有 PLC 连接状态
    /// </summary>
    IEnumerable<PlcConnectionStatus> GetAllConnectionStatuses();

    /// <summary>
    /// 设置轮询间隔
    /// </summary>
    void SetPollingInterval(string plcName, int intervalMs);

    /// <summary>
    /// 获取轮询间隔
    /// </summary>
    int GetPollingInterval(string plcName);
}

/// <summary>
/// PLC 块轮询配置
/// </summary>
public class PlcBlockPollingConfig
{
    public int BlockNumber { get; set; }

    public int Length { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// PLC 连接配置
/// </summary>
public class PlcConnectionPollingConfig
{
    public string PlcName { get; set; } = string.Empty;

    public S7Connection Connection { get; set; } = null!;

    public int IntervalMs { get; set; } = 100;

    public List<PlcBlockPollingConfig> Blocks { get; set; } = new();
}

/// <summary>
/// PLC 轮询服务实现
/// </summary>
public class PlcPollingService : IPlcPollingService
{
    private readonly Dictionary<string, PlcConnectionPollingConfig> _connections = new();
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    private readonly ConcurrentDictionary<string, PlcBlock> _lastBlocks = new();
    private readonly ILogger? _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;

    public PlcPollingService(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void AddPlcConnection(S7Connection connection, int pollingIntervalMs = 100)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var config = new PlcConnectionPollingConfig
        {
            PlcName = connection.PlcName,
            Connection = connection,
            IntervalMs = pollingIntervalMs
        };

        lock (_connections)
        {
            _connections[connection.PlcName] = config;
        }

        _logger?.LogInformation("Added PLC connection: {PlcName} with interval {IntervalMs}ms", 
            connection.PlcName, pollingIntervalMs);
    }

    public void RemovePlcConnection(string plcName)
    {
        ArgumentNullException.ThrowIfNull(plcName);

        lock (_connections)
        {
            if (_connections.Remove(plcName))
            {
                _logger?.LogInformation("Removed PLC connection: {PlcName}", plcName);
            }
        }

        // 停止对应的定时器
        if (_timers.TryRemove(plcName, out var timer))
        {
            timer?.Dispose();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        _logger?.LogInformation("Starting PLC polling service");

        // 连接所有 PLC
        await ConnectAllPlcsAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        // 启动轮询定时器
        StartPollingTimers();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return;

        _logger?.LogInformation("Stopping PLC polling service");

        // 停止所有定时器
        StopPollingTimers();

        // 断开所有连接
        await DisconnectAllPlcsAsync(cancellationToken).ConfigureAwait(false);

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _isRunning = false;

        _logger?.LogInformation("PLC polling service stopped");
    }

    public PlcConnectionStatus? GetConnectionStatus(string plcName)
    {
        ArgumentNullException.ThrowIfNull(plcName);

        lock (_connections)
        {
            if (_connections.TryGetValue(plcName, out var config))
            {
                return config.Connection.GetStatus();
            }
        }

        return null;
    }

    public IEnumerable<PlcConnectionStatus> GetAllConnectionStatuses()
    {
        lock (_connections)
        {
            return _connections.Values
                .Select(c => c.Connection.GetStatus())
                .ToList();
        }
    }

    public void SetPollingInterval(string plcName, int intervalMs)
    {
        ArgumentNullException.ThrowIfNull(plcName);

        if (intervalMs <= 0)
            throw new ArgumentException("Interval must be greater than 0", nameof(intervalMs));

        lock (_connections)
        {
            if (_connections.TryGetValue(plcName, out var config))
            {
                config.IntervalMs = intervalMs;
                _logger?.LogInformation("Set polling interval for {PlcName} to {IntervalMs}ms", 
                    plcName, intervalMs);
            }
        }

        // 重启定时器
        if (_timers.TryRemove(plcName, out var oldTimer))
        {
            oldTimer?.Dispose();
        }

        if (_isRunning)
        {
            StartPollingTimer(plcName);
        }
    }

    public int GetPollingInterval(string plcName)
    {
        ArgumentNullException.ThrowIfNull(plcName);

        lock (_connections)
        {
            if (_connections.TryGetValue(plcName, out var config))
            {
                return config.IntervalMs;
            }
        }

        return -1;
    }

    private async Task ConnectAllPlcsAsync(CancellationToken cancellationToken)
    {
        List<PlcConnectionPollingConfig> configs;
        lock (_connections)
        {
            configs = _connections.Values.ToList();
        }

        foreach (var config in configs)
        {
            try
            {
                var connected = await config.Connection.ConnectAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (connected)
                {
                    _logger?.LogInformation("Connected to PLC: {PlcName}", config.PlcName);
                }
                else
                {
                    _logger?.LogWarning("Failed to connect to PLC: {PlcName}", config.PlcName);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error connecting to PLC: {PlcName}", config.PlcName);
            }
        }
    }

    private async Task DisconnectAllPlcsAsync(CancellationToken cancellationToken)
    {
        List<PlcConnectionPollingConfig> configs;
        lock (_connections)
        {
            configs = _connections.Values.ToList();
        }

        foreach (var config in configs)
        {
            try
            {
                await config.Connection.DisconnectAsync(cancellationToken)
                    .ConfigureAwait(false);

                _logger?.LogInformation("Disconnected from PLC: {PlcName}", config.PlcName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error disconnecting from PLC: {PlcName}", config.PlcName);
            }
        }
    }

    private void StartPollingTimers()
    {
        List<string> plcNames;
        lock (_connections)
        {
            plcNames = _connections.Keys.ToList();
        }

        foreach (var plcName in plcNames)
        {
            StartPollingTimer(plcName);
        }
    }

    private void StartPollingTimer(string plcName)
    {
        PlcConnectionPollingConfig? config;
        lock (_connections)
        {
            _connections.TryGetValue(plcName, out config);
        }

        if (config == null)
            return;

        var timer = new Timer(
            async _ => await PollPlcAsync(config, _cancellationTokenSource?.Token ?? CancellationToken.None),
            null,
            config.IntervalMs,
            config.IntervalMs
        );

        _timers.AddOrUpdate(plcName, timer, (_, oldTimer) =>
        {
            oldTimer?.Dispose();
            return timer;
        });
    }

    private void StopPollingTimers()
    {
        foreach (var timer in _timers.Values)
        {
            timer?.Dispose();
        }

        _timers.Clear();
    }

    private async Task PollPlcAsync(PlcConnectionPollingConfig config, CancellationToken cancellationToken)
    {
        try
        {
            if (!config.Connection.IsConnected)
            {
                // 尝试重新连接
                await config.Connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 轮询所有启用的块
            foreach (var blockConfig in config.Blocks.Where(b => b.Enabled))
            {
                try
                {
                    var data = await config.Connection.ReadBlockAsync(
                        blockConfig.BlockNumber,
                        blockConfig.Length,
                        cancellationToken
                    ).ConfigureAwait(false);

                    if (data != null)
                    {
                        var block = Crc32Helper.WithHash(new PlcBlock
                        {
                            PlcName = config.PlcName,
                            BlockNumber = blockConfig.BlockNumber,
                            Data = data,
                            ReadTime = DateTime.UtcNow,
                            IsValid = true
                        });

                        _lastBlocks[GetBlockKey(config.PlcName, blockConfig.BlockNumber)] = block;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, 
                        "Error reading block {BlockNumber} from PLC {PlcName}", 
                        blockConfig.BlockNumber, config.PlcName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error polling PLC: {PlcName}", config.PlcName);
        }
    }

    private static string GetBlockKey(string plcName, int blockNumber)
    {
        return $"{plcName}:{blockNumber}";
    }

    /// <summary>
    /// 获取缓存的 PLC 块数据
    /// </summary>
    public PlcBlock? GetCachedBlock(string plcName, int blockNumber)
    {
        var key = GetBlockKey(plcName, blockNumber);
        _lastBlocks.TryGetValue(key, out var block);
        return block;
    }
}
